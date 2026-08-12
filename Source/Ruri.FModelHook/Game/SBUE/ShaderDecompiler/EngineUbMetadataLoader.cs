using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CUE4Parse.UE4.Versions;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

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

        List<string> scanRoots = BuildScanRoots(directory, gameVersionEnum, tryBaseFallback);

        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in scanRoots)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*_MetaData.json", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/_ShaderType/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenFiles.Add(Path.GetFullPath(file))) continue;
                if (TryLoadFile(file, byNameAndHash, hashesByName, logError)) loaded++;
                else skipped++;
            }
        }

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[EngineUbMetadata] Loaded {loaded} layout(s){gameTag} from '{directory}' ({skipped} skipped). Scan roots: {string.Join(" -> ", scanRoots)}");

        VerifySeedHashesForDiagnostics(byNameAndHash, log);

        return new EngineUbMetadataRegistry(directory, byNameAndHash, hashesByName);
    }

    internal static uint ComputeLayoutHash(uint constantBufferSize, byte bindingFlags, bool hasStaticSlot, IReadOnlyList<EngineUbResourceSlot> resources, bool ue55Plus = false)
    {
        uint h = ((constantBufferSize & 0xFFFFu) << 16) | ((uint)bindingFlags << 8) | (uint)(hasStaticSlot ? 1 : 0);
        for (int i = 0; i < resources.Count; i++)
            h ^= (uint)(resources[i].Offset & 0xFFFFu);
        int n = resources.Count;
        while (n >= 4)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 0;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 8;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 16;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 24;
        }
        while (n >= 2)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 0;
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF) << 16;
        }
        while (n > 0)
        {
            n--; h ^= (uint)(UbmtValue(resources[n].UbmtType, ue55Plus) & 0xFF);
        }
        return h;
    }

    private static int UbmtValue(string typeName, bool ue55Plus)
    {
        if (typeName == "UBMT_RDG_TEXTURE_NON_PIXEL_SRV") return ue55Plus ? 13 : 12;        if (typeName == "UBMT_RESOURCE_COLLECTION")        return ue55Plus ? 24 : 22;
        int v = typeName switch
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
            _ => -1,        };
        if (ue55Plus && v >= 13) v += 1;
        return v;
    }

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
            uint cbSize = (uint)meta.ConstantBufferSize;
            bool ue55Plus = SeedIsUe55Plus(meta.EngineVersion);
            uint computedAsIs = ComputeLayoutHash(cbSize, bf, hasStaticSlot, meta.Resources, ue55Plus);
            if (computedAsIs == declared)
            {
                matched++;
                continue;
            }
            uint aligned16 = (cbSize + 15u) & ~15u;
            uint computedAligned = ComputeLayoutHash(aligned16, bf, hasStaticSlot, meta.Resources, ue55Plus);
            mismatched++;
            log($"[EngineUbMetadata][HashVerify] MISMATCH name={meta.Name} declared=0x{declared:X8} computed(cbsize={cbSize})=0x{computedAsIs:X8}  align16(cbsize={aligned16})=0x{computedAligned:X8}{(computedAligned == declared ? "  <- align16 reproduces declared" : "")}");
        }
        log($"[EngineUbMetadata][HashVerify] {matched} matched, {mismatched} mismatched of {byNameAndHash.Count} loaded seeds.");
    }

    private static bool SeedIsUe55Plus(string? engineVersion)
    {
        if (string.IsNullOrWhiteSpace(engineVersion)) return false;
        string[] parts = engineVersion.Split('.');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[0], out int major)
            && int.TryParse(parts[1], out int minor)
            && (major > 5 || (major == 5 && minor >= 5));
    }

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

    internal static bool TryDeriveBaseUeFromEGameForShaderTypes(string gameVersionEnum, out string baseUeName)
        => TryDeriveBaseUeFromEGame(gameVersionEnum, out baseUeName);

    internal static List<string> BuildScanRoots(string directory, string? gameVersionEnum, bool tryBaseFallback)
    {
        List<string> scanRoots = new();
        bool foundVersionScoped = false;

        if (!string.IsNullOrEmpty(gameVersionEnum))
        {
            string specific = Path.Combine(directory, gameVersionEnum);
            if (Directory.Exists(specific)) { scanRoots.Add(specific); foundVersionScoped = true; }
        }

        bool gameIsBaseUe = !string.IsNullOrEmpty(gameVersionEnum)
            && gameVersionEnum.StartsWith("GAME_UE", StringComparison.Ordinal);
        if ((gameIsBaseUe || tryBaseFallback)
            && TryGetEngineMajorMinor(gameVersionEnum, out int major, out int minor))
        {
            string baseEnumDir = Path.Combine(directory, $"GAME_UE{major}_{minor}");
            if (Directory.Exists(baseEnumDir) && !scanRoots.Contains(baseEnumDir))
            {
                scanRoots.Add(baseEnumDir);
                foundVersionScoped = true;
            }
            foreach (string verDir in EnumerateVersionStringFolders(directory, major, minor))
            {
                if (!scanRoots.Contains(verDir)) { scanRoots.Add(verDir); foundVersionScoped = true; }
            }
        }

        if (!foundVersionScoped) scanRoots.Add(directory);
        return scanRoots;
    }

    internal static bool TryGetEngineMajorMinor(string? gameVersionEnum, out int major, out int minor)
    {
        major = 0; minor = 0;
        if (string.IsNullOrEmpty(gameVersionEnum)) return false;
        string baseName = gameVersionEnum;
        if (!baseName.StartsWith("GAME_UE", StringComparison.Ordinal)
            && !TryDeriveBaseUeFromEGame(gameVersionEnum, out baseName))
        {
            return false;
        }
        const string prefix = "GAME_UE";
        if (!baseName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string rest = baseName.Substring(prefix.Length);        int underscore = rest.IndexOf('_');
        if (underscore <= 0 || underscore >= rest.Length - 1) return false;
        return int.TryParse(rest.AsSpan(0, underscore), out major)
            && int.TryParse(rest.AsSpan(underscore + 1), out minor);
    }

    internal static IEnumerable<string> EnumerateVersionStringFolders(string directory, int major, int minor)
    {
        string exact = $"{major}.{minor}";
        string prefix = exact + ".";
        foreach (string dir in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(dir);
            if (string.Equals(name, exact, StringComparison.Ordinal)
                || name.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return dir;
            }
        }
    }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static bool TryLoadFile(string file, Dictionary<(string, uint), EngineUbMetadata> byNameAndHash, Dictionary<string, List<uint>> hashesByName, Action<string>? logError)
    {
        JsonSerializerOptions jsonOpts = s_jsonOpts;
        try
        {
            string json = File.ReadAllText(file);
            EngineUbMetadata? entry = JsonSerializer.Deserialize<EngineUbMetadata>(json, jsonOpts);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LayoutHashHex))
            {
                logError?.Invoke($"[EngineUbMetadata] {file}: missing 'name' or 'layoutHash' — skipped.");
                return false;
            }
            EnsureTypedBucketsPopulated(entry);
            uint hash = entry.ParsedHash();
            var key = (entry.Name, hash);
            if (byNameAndHash.ContainsKey(key))
            {
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

    private static void EnsureTypedBucketsPopulated(EngineUbMetadata meta)
    {
        if (meta.Resources.Count == 0) return;
        bool anyBucket = meta.Textures.Count > 0 || meta.Samplers.Count > 0
                       || meta.Buffers.Count > 0  || meta.UAVs.Count > 0;
        if (anyBucket) return;
        foreach (EngineUbResourceSlot slot in meta.Resources)
        {
            switch (slot.UbmtType)
            {
                case "UBMT_TEXTURE":
                case "UBMT_RDG_TEXTURE":
                case "UBMT_RDG_TEXTURE_ACCESS":
                case "UBMT_RDG_TEXTURE_ACCESS_ARRAY":
                    meta.Textures.Add(new TextureParameter
                    {
                        Name = slot.Name,
                        NameIndex = -1,
                        Index = slot.Index,
                        SamplerIndex = -1,
                        MultiSampled = false,
                        Dim = 2,
                    });
                    break;
                case "UBMT_SAMPLER":
                    meta.Samplers.Add(new SamplerParameter
                    {
                        Name = slot.Name,
                        Sampler = (uint)slot.Index,
                        BindPoint = slot.Index,
                    });
                    break;
                case "UBMT_UAV":
                case "UBMT_RDG_TEXTURE_UAV":
                case "UBMT_RDG_BUFFER_UAV":
                    meta.UAVs.Add(new UAVParameter
                    {
                        Name = slot.Name,
                        NameIndex = -1,
                        Index = slot.Index,
                        OriginalIndex = slot.Index,
                    });
                    break;
                default:                    meta.Buffers.Add(new BufferBindingParameter
                    {
                        Name = slot.Name,
                        NameIndex = -1,
                        Index = slot.Index,
                        ArraySize = 0,
                    });
                    break;
            }
        }
    }

    public EngineUbMetadata? Lookup(string ubName, uint layoutHash)
    {
        if (string.IsNullOrEmpty(ubName)) return null;
        return _byNameAndHash.TryGetValue((ubName, layoutHash), out EngineUbMetadata? meta) ? meta : null;
    }

    public EngineUbMetadata? LookupByHashOnly(uint layoutHash)
    {
        EngineUbMetadata? hit = null;
        foreach (var kvp in _byNameAndHash)
        {
            if (kvp.Key.Hash != layoutHash) continue;
            if (hit != null) return null;            hit = kvp.Value;
        }
        return hit;
    }

    public EngineUbMetadata? LookupByNameWithSizeCap(string ubName, int cookCbSize)
    {
        if (string.IsNullOrEmpty(ubName) || cookCbSize <= 0) return null;
        EngineUbMetadata? best = null;
        int bestSize = -1;
        foreach (var kvp in _byNameAndHash)
        {
            if (!string.Equals(kvp.Key.Name, ubName, StringComparison.Ordinal)) continue;
            int seedSize = kvp.Value.ConstantBufferSize;
            if (seedSize <= 0 || seedSize > cookCbSize) continue;
            if (seedSize > bestSize)
            {
                best = kvp.Value;
                bestSize = seedSize;
            }
        }
        return best;
    }

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

internal static class EngineUbMetadataTranslator
{
    public static ConstantBufferParameter ToConstantBufferParameter(EngineUbMetadata meta)
    {
        if (meta.ConstantBuffer != null)
        {
            if (string.IsNullOrWhiteSpace(meta.ConstantBuffer.Name))
                meta.ConstantBuffer.Name = meta.Name;
            return meta.ConstantBuffer;
        }
        return new ConstantBufferParameter
        {
            Name = meta.Name,
            NameIndex = -1,
            VectorParameters = Array.Empty<VectorParameter>(),
            MatrixParameters = Array.Empty<MatrixParameter>(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = 0,
            IsPartialCB = false,
        };
    }
}
