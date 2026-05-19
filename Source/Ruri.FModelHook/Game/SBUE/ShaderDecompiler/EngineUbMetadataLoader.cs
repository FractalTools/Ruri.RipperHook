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

        // Diagnostic: re-compute the hash from each loaded seed and compare to
        // its declared layoutHash. Surfaces seed files where
        // `constantBufferSize` is internally inconsistent (i.e. the value in
        // the JSON would NOT reproduce the declared hash through
        // FRHIUniformBufferLayoutInitializer::ComputeHash). This is a strict
        // self-check on the seed authoring — never affects lookup behaviour.
        VerifySeedHashesForDiagnostics(byNameAndHash, log);

        return new EngineUbMetadataRegistry(directory, byNameAndHash, hashesByName);
    }

    // Mirrors `FRHIUniformBufferLayoutInitializer::ComputeHash` byte-for-byte
    // (UE 5.1 `RHIResources.h` / UE 5.4 `RHIUniformBufferLayoutInitializer.h`).
    // Inputs are exactly what the engine folds in: ConstantBufferSize (uint16
    // masked low bits), BindingFlags (uint8), `StaticSlot != INVALID` bit,
    // then for each resource MemberOffset (uint16) plus a 4/2/1 byte XOR fold
    // of MemberType consuming Resources from the END. Returns 0xFFFFFFFF if
    // any resource has an unknown UBMT_* type (silent for diagnostics — the
    // caller logs the mismatch).
    internal static uint ComputeLayoutHash(uint constantBufferSize, byte bindingFlags, bool hasStaticSlot, IReadOnlyList<EngineUbResource> resources)
    {
        uint h = ((constantBufferSize & 0xFFFFu) << 16) | ((uint)bindingFlags << 8) | (uint)(hasStaticSlot ? 1 : 0);
        for (int i = 0; i < resources.Count; i++)
            h ^= (uint)(resources[i].Offset & 0xFFFFu);
        int n = resources.Count;
        // Consume from the END in groups of 4/2/1, matching the unrolled fold
        // in UE's source.
        while (n >= 4)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF) << 0;
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF) << 8;
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF) << 16;
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF) << 24;
        }
        while (n >= 2)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF) << 0;
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF) << 16;
        }
        while (n > 0)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].Type) & 0xFF);
        }
        return h;
    }

    // EUniformBufferBaseType (RHIDefinitions.h:1414). Same values in 5.1 and 5.4.
    private static int UbmtValue(string typeName) => typeName switch
    {
        "UBMT_INVALID"                       => 0,
        "UBMT_BOOL"                          => 1,
        "UBMT_INT32"                         => 2,
        "UBMT_UINT32"                        => 3,
        "UBMT_FLOAT32"                       => 4,
        "UBMT_TEXTURE"                       => 5,
        "UBMT_SRV"                           => 6,
        "UBMT_UAV"                           => 7,
        "UBMT_SAMPLER"                       => 8,
        "UBMT_RDG_TEXTURE"                   => 9,
        "UBMT_RDG_TEXTURE_ACCESS"            => 10,
        "UBMT_RDG_TEXTURE_ACCESS_ARRAY"      => 11,
        "UBMT_RDG_TEXTURE_SRV"               => 12,
        "UBMT_RDG_TEXTURE_UAV"               => 13,
        "UBMT_RDG_BUFFER_ACCESS"             => 14,
        "UBMT_RDG_BUFFER_ACCESS_ARRAY"       => 15,
        "UBMT_RDG_BUFFER_SRV"                => 16,
        "UBMT_RDG_BUFFER_UAV"                => 17,
        "UBMT_RDG_UNIFORM_BUFFER"            => 18,
        "UBMT_NESTED_STRUCT"                 => 19,
        "UBMT_INCLUDED_STRUCT"               => 20,
        "UBMT_REFERENCED_STRUCT"             => 21,
        "UBMT_RENDER_TARGET_BINDING_SLOTS"   => 22,
        _ => -1, // unknown — hash will diverge, surfaces as MISMATCH in log
    };

    private static byte BindingFlagsValue(string name) => name switch
    {
        "Shader" => 1,
        "Static" => 2,
        "StaticAndShader" => 3,
        _ => 1,
    };

    private static void VerifySeedHashesForDiagnostics(Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Action<string>? log)
    {
        if (log == null) return;
        int matched = 0, mismatched = 0;
        foreach (var kvp in byNameAndHash)
        {
            EngineUbMetadata meta = kvp.Value;
            uint declared = kvp.Key.Item2;
            byte bf = BindingFlagsValue(meta.BindingFlags);
            bool hasStaticSlot = string.Equals(meta.BindingFlags, "Static", StringComparison.Ordinal)
                              || string.Equals(meta.BindingFlags, "StaticAndShader", StringComparison.Ordinal);
            uint computedAsIs = ComputeLayoutHash(meta.ConstantBufferSize, bf, hasStaticSlot, meta.Resources);
            if (computedAsIs == declared)
            {
                matched++;
                continue;
            }
            // Probe: try common alignments (round up to 16) — if the seed
            // recorded the unaligned numeric end but the engine folded the
            // aligned C++ struct sizeof, this catches the off-by-padding.
            uint aligned16 = (meta.ConstantBufferSize + 15u) & ~15u;
            uint computedAligned = ComputeLayoutHash(aligned16, bf, hasStaticSlot, meta.Resources);
            mismatched++;
            log($"[EngineUbMetadata][HashVerify] MISMATCH name={meta.Name} declared=0x{declared:X8} computed(cbsize={meta.ConstantBufferSize})=0x{computedAsIs:X8}  align16(cbsize={aligned16})=0x{computedAligned:X8}{(computedAligned == declared ? "  <- align16 reproduces declared" : "")}");
        }
        log($"[EngineUbMetadata][HashVerify] {matched} matched, {mismatched} mismatched of {byNameAndHash.Count} loaded seeds.");
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
