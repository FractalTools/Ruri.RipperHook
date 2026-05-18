using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[EngineUbMetadata] Directory not set or missing: {directory ?? "<null>"} — engine UB members will stay anonymous.");
            return Empty;
        }

        Dictionary<(string, uint), EngineUbMetadata> byNameAndHash = new();
        Dictionary<string, List<uint>> hashesByName = new(StringComparer.Ordinal);
        int loaded = 0, skipped = 0;
        JsonSerializerOptions jsonOpts = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

        foreach (string file in Directory.EnumerateFiles(directory, "*_MetaData.json", SearchOption.AllDirectories))
        {
            try
            {
                string json = File.ReadAllText(file);
                EngineUbMetadata? entry = JsonSerializer.Deserialize<EngineUbMetadata>(json, jsonOpts);
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LayoutHashHex))
                {
                    logError?.Invoke($"[EngineUbMetadata] {file}: missing 'name' or 'layoutHash' — skipped.");
                    skipped++; continue;
                }
                uint hash = entry.ParsedHash();
                var key = (entry.Name, hash);
                if (byNameAndHash.ContainsKey(key))
                {
                    logError?.Invoke($"[EngineUbMetadata] {file}: duplicate (name={entry.Name}, hash=0x{hash:X8}) already loaded — skipped.");
                    skipped++; continue;
                }
                byNameAndHash[key] = entry;
                if (!hashesByName.TryGetValue(entry.Name, out List<uint>? list))
                {
                    list = new List<uint>();
                    hashesByName[entry.Name] = list;
                }
                list.Add(hash);
                loaded++;
            }
            catch (Exception ex)
            {
                logError?.Invoke($"[EngineUbMetadata] {file}: parse failed — {ex.GetType().Name}: {ex.Message}");
                skipped++;
            }
        }

        log?.Invoke($"[EngineUbMetadata] Loaded {loaded} layout(s) from '{directory}' ({skipped} skipped).");
        return new EngineUbMetadataRegistry(directory, byNameAndHash, hashesByName);
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
