using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CUE4Parse.UE4.Versions;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Loads engine-UB metadata JSONs from a directory and serves
// `(UBName, LayoutHash)` lookups. Filename convention enforced for
// O(1) hash-keyed dispatch even with hundreds of files; full directory
// scan only happens once at startup.
//
// Resolution is hash-first:
//   - If `(name, hash)` matches a loaded file: use it (canonical hit).
//   - Else if `name` matches at least one file but hash differs: log
//     a "shape drift" warning (engine version mismatch likely) and
//     return null so the caller emits a placeholder. Never emit a
//     wrong name silently.
//   - Else: return null (no metadata for this UB).
internal sealed class EngineUbMetadataRegistry
{
    private readonly Dictionary<(string Name, uint Hash), EngineUbMetadata> _byNameAndHash;
    private readonly Dictionary<string, List<uint>> _hashesByName;

    public string SourceDirectory { get; }
    public int FileCount => _byNameAndHash.Count;

    private EngineUbMetadataRegistry(string sourceDir, Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Dictionary<string, List<uint>> hashesByName)
    {
        SourceDirectory = sourceDir;
        _byNameAndHash = byNameAndHash;
        _hashesByName = hashesByName;
    }

    public static EngineUbMetadataRegistry Empty { get; } = new(string.Empty,
        new Dictionary<(string, uint), EngineUbMetadata>(),
        new Dictionary<string, List<uint>>(StringComparer.Ordinal));

    public static EngineUbMetadataRegistry Load(string? directory, Action<string>? log = null, Action<string>? logError = null)
        => LoadForGame(directory, gameVersionEnum: null, tryBaseFallback: true, log, logError);

    // Game-aware loader. When `gameVersionEnum` is set (e.g. "GAME_InfinityNikki"
    // or "GAME_UE5_4" as captured from FModel's EGame enum at export time):
    //   1. Loads from `<directory>/<gameVersionEnum>/` FIRST (game-specific overrides
    //      — modded UEs, project-specific UB layouts).
    //   2. When `tryBaseFallback` is true and the game enum doesn't already start
    //      with `GAME_UE`, loads from `<directory>/<base UE folder>/`
    //      (e.g. GAME_UE5_4 derived from GAME_InfinityNikki = GAME_UE5_4 + 2)
    //      — base UE layouts shared by all games on that engine version.
    //      Toggleable from the user's `ShaderDecompilerSettings.TryMatchBaseEngineVersion`:
    //      most games (~99%) don't customize CB layouts so this is virtually
    //      always correct and dramatically reduces manual seeding work; the
    //      flag exists to opt out for the rare modded engine where the base
    //      seeds would silently mis-name a drifted layout.
    //   3. Finally scans any other files under `<directory>` (recursive) for
    //      hand-organised metadata that doesn't follow the GAME_<X> convention.
    // Earlier sources take precedence on (Name, Hash) collision — the
    // game-specific folder wins over the base UE folder.
    //
    // When `gameVersionEnum` is null/empty, falls back to a single recursive
    // scan of `directory` (legacy behaviour) — every JSON is loaded with
    // no priority discrimination.
    public static EngineUbMetadataRegistry LoadForGame(string? directory, string? gameVersionEnum, bool tryBaseFallback = true, Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[EngineUbMetadata] Directory not set or missing: {directory ?? "<null>"} — engine UB members will stay anonymous.");
            return Empty;
        }

        Dictionary<(string, uint), EngineUbMetadata> byNameAndHash = new();
        Dictionary<string, List<uint>> hashesByName = new(StringComparer.Ordinal);
        int loaded = 0, skipped = 0;

        // Build a prioritised scan list: game-specific folder first, then
        // base UE folder (only when the toggle is on and the enum names a
        // game-specific derivative), then a recursive sweep of anything
        // else under root.
        List<string> scanRoots = new();
        if (!string.IsNullOrEmpty(gameVersionEnum))
        {
            string specific = Path.Combine(directory, gameVersionEnum);
            if (Directory.Exists(specific)) scanRoots.Add(specific);
        }
        if (tryBaseFallback
            && !string.IsNullOrEmpty(gameVersionEnum)
            && !gameVersionEnum.StartsWith("GAME_UE", StringComparison.Ordinal)
            && TryDeriveBaseUeFromEGame(gameVersionEnum, out string baseUe)
            && !string.Equals(baseUe, gameVersionEnum, StringComparison.Ordinal))
        {
            string baseDir = Path.Combine(directory, baseUe);
            if (Directory.Exists(baseDir)) scanRoots.Add(baseDir);
        }
        scanRoots.Add(directory); // recursive sweep — catches everything else, idempotent on dupe (Name,Hash)

        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in scanRoots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*_MetaData.json", SearchOption.AllDirectories))
            {
                if (!seenFiles.Add(Path.GetFullPath(file))) continue;
                if (TryLoadFile(file, byNameAndHash, hashesByName, logError)) loaded++;
                else skipped++;
            }
        }

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[EngineUbMetadata] Loaded {loaded} layout(s){gameTag} from '{directory}' ({skipped} skipped). Scan roots: {string.Join(" -> ", scanRoots)}");
        return new EngineUbMetadataRegistry(directory, byNameAndHash, hashesByName);
    }

    // Derives the base UE major.minor `EGame` name (e.g. "GAME_UE5_4")
    // for any game-specific value (e.g. "GAME_InfinityNikki") by enum
    // arithmetic — no hand-maintained string table.
    //
    // EGame encoding (CUE4Parse/UE4/Versions/EGame.cs:225-229):
    //   GameUe<F>Base = 0x<F>000000
    //   GAME_UE<F>_<X>     = GameUe<F>Base + (X << 16)   // base versions
    //   GAME_<GameSpecific> = GAME_UE<F>_<X> + n         // n=1..255 offset
    // Masking with 0xFFFF0000 keeps family + major.minor bits and drops
    // the per-game offset, yielding the parent base unchanged. Casting
    // back to EGame and ToString() round-trips to the base member name
    // because every game-specific value's parent is by construction a
    // declared base — and when CUE4Parse adds new entries this stays
    // correct automatically.
    private static bool TryDeriveBaseUeFromEGame(string gameVersionEnum, out string baseUeName)
    {
        baseUeName = string.Empty;
        if (!Enum.TryParse<EGame>(gameVersionEnum, ignoreCase: false, out EGame game)) return false;
        EGame baseValue = (EGame)((uint)game & 0xFFFF0000u);
        string asName = baseValue.ToString();
        if (!asName.StartsWith("GAME_UE", StringComparison.Ordinal)) return false;
        baseUeName = asName;
        return true;
    }

    private static bool TryLoadFile(string file, Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Dictionary<string, List<uint>> hashesByName, Action<string>? logError)
    {
        JsonSerializerOptions jsonOpts = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };
        try
        {
            string json = File.ReadAllText(file);
            EngineUbMetadata? entry = JsonSerializer.Deserialize<EngineUbMetadata>(json, jsonOpts);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LayoutHashHex))
            {
                logError?.Invoke($"[EngineUbMetadata] {file}: missing 'name' or 'layoutHash' — skipped.");
                return false;
            }
            uint hash = entry.ParsedHash();
            var key = (entry.Name, hash);
            if (byNameAndHash.ContainsKey(key))
            {
                // Silent skip — the game-specific folder already won this
                // (Name, Hash). Hand-organised dupes elsewhere in the tree
                // are absorbed without warning so the user can keep
                // experimental copies next to the seeds.
                return false;
            }
            byNameAndHash[key] = entry;
            if (!hashesByName.TryGetValue(entry.Name, out List<uint>? list))
            {
                list = new List<uint>();
                hashesByName[entry.Name] = list;
            }
            list.Add(hash);
            return true;
        }
        catch (Exception ex)
        {
            logError?.Invoke($"[EngineUbMetadata] {file}: parse failed — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public EngineUbMetadata? Lookup(string ubName, uint layoutHash)
    {
        if (string.IsNullOrEmpty(ubName)) return null;
        return _byNameAndHash.TryGetValue((ubName, layoutHash), out EngineUbMetadata? meta) ? meta : null;
    }

    // For diagnostics: returns true iff at least one file matches `ubName`
    // (any hash). Used by the symbolizer to log "shape-drift" warnings:
    // we have metadata for `View` but the cook's hash doesn't match any
    // of them — almost certainly a different engine version layout.
    public bool HasAnyForName(string ubName, out IReadOnlyList<uint> knownHashes)
    {
        if (_hashesByName.TryGetValue(ubName, out List<uint>? list))
        {
            knownHashes = list;
            return true;
        }
        knownHashes = Array.Empty<uint>();
        return false;
    }
}

// Translates an EngineUbMetadata into the SerializedProgramData shape the
// patcher / rewriter consume — same flat list of VectorParameter /
// MatrixParameter the MaterialConstantBufferReader produces for Material.
internal static class EngineUbMetadataTranslator
{
    public static ConstantBufferParameter ToConstantBufferParameter(EngineUbMetadata meta)
    {
        List<VectorParameter> vectorParams = new();
        List<MatrixParameter> matrixParams = new();

        foreach (EngineUbNumericMember m in meta.Members)
        {
            if (string.IsNullOrWhiteSpace(m.Name)) continue;
            if (!ParseType(m.Type, out ShaderParamType scalar, out int rows, out int cols, out bool isMatrix)) continue;

            if (isMatrix)
            {
                matrixParams.Add(new MatrixParameter
                {
                    Name = m.Name,
                    NameIndex = -1,
                    Type = scalar,
                    Index = checked((int)m.Offset),
                    ArraySize = m.ArraySize,
                    RowCount = unchecked((byte)rows),
                    ColumnCount = unchecked((byte)cols),
                    IsMatrix = true,
                });
            }
            else
            {
                vectorParams.Add(new VectorParameter
                {
                    Name = m.Name,
                    NameIndex = -1,
                    Type = scalar,
                    Index = checked((int)m.Offset),
                    ArraySize = m.ArraySize,
                    IsMatrix = false,
                    RowCount = unchecked((byte)rows),
                    ColumnCount = unchecked((byte)cols),
                });
            }
        }

        return new ConstantBufferParameter
        {
            Name = meta.Name,
            NameIndex = -1,
            VectorParameters = vectorParams.OrderBy(static p => p.Index).ToArray(),
            MatrixParameters = matrixParams.OrderBy(static p => p.Index).ToArray(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = checked((int)meta.ConstantBufferSize),
            IsPartialCB = false,
        };
    }

    // Returns false on unrecognized type. On true, scalar/rows/cols/isMatrix
    // are populated. ShaderParamType has no Unknown value, so we signal via
    // bool — matches the existing MaterialConstantBufferReader convention.
    //
    // Strict on HLSL-style names: `Float`, `Float2`, `Float4`, `Float4x4`,
    // `Int4`, `UInt3`, `Bool`, `Half2`, etc. Seed JSONs must follow this
    // convention — UE C++ type names (`FMatrix44f`, `FVector4f`,
    // `FLinearColor`, `FIntPoint`, …) are NOT accepted; emit the HLSL
    // form in the JSON instead. Keeping the parser strict surfaces seed
    // mistakes as missing members rather than silently absorbing a
    // sprawling alias table.
    private static bool ParseType(string type, out ShaderParamType scalar, out int rows, out int cols, out bool isMatrix)
    {
        scalar = ShaderParamType.Float; rows = 0; cols = 0; isMatrix = false;
        if (string.IsNullOrWhiteSpace(type)) return false;
        string t = type.Trim();
        // Matrix forms first (else "Float4" matches before "Float4x4").
        int xPos = t.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (xPos > 0 && xPos < t.Length - 1)
        {
            string lhs = t.Substring(0, xPos);
            string rhs = t.Substring(xPos + 1);
            if (TryParseScalarWithRows(lhs, out scalar, out rows) && int.TryParse(rhs, out cols))
            {
                isMatrix = true;
                return true;
            }
        }
        if (TryParseScalarWithRows(t, out scalar, out rows))
        {
            cols = 1;
            isMatrix = false;
            return true;
        }
        return false;
    }

    private static bool TryParseScalarWithRows(string t, out ShaderParamType scalar, out int rows)
    {
        rows = 1;
        scalar = ShaderParamType.Float;
        string lower = t.ToLowerInvariant();
        string baseName;
        if      (lower.StartsWith("float")) { baseName = "float"; scalar = ShaderParamType.Float; }
        else if (lower.StartsWith("uint"))  { baseName = "uint";  scalar = ShaderParamType.UInt;  }
        else if (lower.StartsWith("int"))   { baseName = "int";   scalar = ShaderParamType.Int;   }
        else if (lower.StartsWith("bool"))  { baseName = "bool";  scalar = ShaderParamType.Bool;  }
        else if (lower.StartsWith("half"))  { baseName = "half";  scalar = ShaderParamType.Half;  }
        else return false;

        string suffix = t.Substring(baseName.Length);
        if (string.IsNullOrEmpty(suffix)) return true;     // scalar
        if (int.TryParse(suffix, out int n) && n is >= 1 and <= 4) { rows = n; return true; }
        return false;
    }
}
