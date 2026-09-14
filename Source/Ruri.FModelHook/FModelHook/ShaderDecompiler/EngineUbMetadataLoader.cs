using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CUE4Parse.UE4.Versions;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// Every uniform buffer layout the engine dumps stated for one engine version, looked up the way
/// a cooked shader identifies one.
///
/// A shader names each buffer it binds and, for every buffer that binds a resource through the
/// resource table, the hash the engine stamped on that buffer's layout. That hash is the only
/// statement that the layout a seed describes is the layout the shader was compiled against, so
/// it is matched exactly -- except for the one byte a 4.x engine fills with the buffer's static
/// slot index, which is assigned at engine start and which the seed cannot know. A buffer of
/// constants alone binds nothing through the table, so its slot carries no hash at all; such a
/// buffer is matched by name against a seed that likewise holds no resources, which is the
/// one thing the shader does state about it.
///
/// Nothing here recomputes a hash: the dumper computed it from the engine's own formula, and a
/// second copy of that formula in the reader would only be a second place for it to drift.
/// </summary>
internal sealed class EngineUbMetadataRegistry
{
    private readonly Dictionary<string, List<EngineUbMetadata>> _byName;
    private readonly int _count;

    public string SourceDirectory { get; }
    public int FileCount => _count;

    private EngineUbMetadataRegistry(string sourceDir, Dictionary<string, List<EngineUbMetadata>> byName, int count)
    {
        SourceDirectory = sourceDir;
        _byName = byName;
        _count = count;
    }

    public static EngineUbMetadataRegistry Empty { get; } = new(string.Empty, new Dictionary<string, List<EngineUbMetadata>>(StringComparer.Ordinal), 0);

    public static EngineUbMetadataRegistry Load(string? directory, Action<string>? log = null, Action<string>? logError = null)
        => LoadForGame(directory, gameVersionEnum: null, tryBaseFallback: true, log, logError);

    public static EngineUbMetadataRegistry LoadForGame(string? directory, string? gameVersionEnum, bool tryBaseFallback = true, Action<string>? log = null, Action<string>? logError = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            log?.Invoke($"[EngineUbMetadata] Directory not set or missing: {directory ?? "<null>"} — engine UB members will stay anonymous.");
            return Empty;
        }

        Dictionary<string, List<EngineUbMetadata>> byName = new(StringComparer.Ordinal);
        HashSet<(string Name, uint Hash)> seen = new();
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
                if (TryLoadFile(file, byName, seen, logError)) loaded++;
                else skipped++;
            }
        }

        string gameTag = string.IsNullOrEmpty(gameVersionEnum) ? "" : $" for game={gameVersionEnum}";
        log?.Invoke($"[EngineUbMetadata] Loaded {loaded} layout(s){gameTag} from '{directory}' ({skipped} skipped). Scan roots: {string.Join(" -> ", scanRoots)}");

        return new EngineUbMetadataRegistry(directory, byName, loaded);
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
        string rest = baseName.Substring(prefix.Length);
        int underscore = rest.IndexOf('_');
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

    private static bool TryLoadFile(string file, Dictionary<string, List<EngineUbMetadata>> byName, HashSet<(string, uint)> seen, Action<string>? logError)
    {
        try
        {
            string json = File.ReadAllText(file);
            EngineUbMetadata? entry = JsonSerializer.Deserialize<EngineUbMetadata>(json, s_jsonOpts);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.LayoutHashHex))
            {
                logError?.Invoke($"[EngineUbMetadata] {file}: missing 'name' or 'layoutHash' — skipped.");
                return false;
            }
            EnsureTypedBucketsPopulated(entry);
            if (!seen.Add((entry.Name, entry.ParsedHash())))
            {
                return false;
            }
            if (!byName.TryGetValue(entry.Name, out List<EngineUbMetadata>? list))
            {
                list = new List<EngineUbMetadata>();
                byName[entry.Name] = list;
            }
            list.Add(entry);
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
                default:
                    meta.Buffers.Add(new BufferBindingParameter
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

    /// <summary>The seed a shader's buffer name and layout hash identify, or null when none does.</summary>
    public EngineUbMetadata? Lookup(string ubName, uint layoutHash)
    {
        if (string.IsNullOrEmpty(ubName) || !_byName.TryGetValue(ubName, out List<EngineUbMetadata>? candidates))
        {
            return null;
        }
        foreach (EngineUbMetadata seed in candidates)
        {
            if (seed.Matches(layoutHash))
            {
                return seed;
            }
        }
        return null;
    }

    /// <summary>The one seed a layout hash identifies whatever the buffer was called, or null when none or several do.</summary>
    public EngineUbMetadata? LookupByHashOnly(uint layoutHash)
    {
        EngineUbMetadata? hit = null;
        foreach (List<EngineUbMetadata> candidates in _byName.Values)
        {
            foreach (EngineUbMetadata seed in candidates)
            {
                if (!seed.Matches(layoutHash)) continue;
                if (hit != null) return null;
                hit = seed;
            }
        }
        return hit;
    }

    /// <summary>
    /// The seed for a buffer the shader binds no resource of, which is why its slot carries no
    /// hash: the one seed of that name that likewise holds only constants, or null when there is
    /// none or more than one -- a seed with resources cannot be the buffer the shader described.
    /// </summary>
    public EngineUbMetadata? LookupConstantsOnly(string ubName)
    {
        if (string.IsNullOrEmpty(ubName) || !_byName.TryGetValue(ubName, out List<EngineUbMetadata>? candidates))
        {
            return null;
        }
        EngineUbMetadata? hit = null;
        foreach (EngineUbMetadata seed in candidates)
        {
            if (seed.Resources.Count > 0) continue;
            if (hit != null) return null;
            hit = seed;
        }
        return hit;
    }

    public bool HasAnyForName(string ubName, out IReadOnlyList<uint> knownHashes)
    {
        if (_byName.TryGetValue(ubName, out List<EngineUbMetadata>? list))
        {
            knownHashes = list.Select(static seed => seed.ParsedHash()).ToList();
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
