using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Newtonsoft.Json;
using NewtonsoftJsonSerializer = Newtonsoft.Json.JsonSerializer;
using JsonTextReader = Newtonsoft.Json.JsonTextReader;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass140_LoadUnifiedMetadataIndex
{
    public static void DoPass(PipelineState state)
    {
        string? unifiedPath = state.Options.UnifiedMetadataPath;
        if (string.IsNullOrEmpty(unifiedPath) || !File.Exists(unifiedPath))
        {
            state.Log("    UnifiedShaderMetadata.json: missing.");
            return;
        }

        long length = new FileInfo(unifiedPath).Length;

        string? materialFilter = state.Options.MaterialFilter;
        bool filtered = !string.IsNullOrWhiteSpace(materialFilter);
        bool lean = length > MaxFullReadBytes && !filtered;
        if (lean)
        {
            state.Log($"    UnifiedShaderMetadata.json: {length / (1024 * 1024)} MB (> {MaxFullReadBytes / (1024 * 1024)} MB) — lean read: package + Niagara hash bridges only, per-material inline bridge skipped.");
        }
        else if (filtered && length > MaxFullReadBytes)
        {
            state.Log($"    UnifiedShaderMetadata.json: {length / (1024 * 1024)} MB,但有 --material-filter「{materialFilter}」—— 逐条流式过滤读取 per-material bridge(内存有界)。");
        }

        UnifiedRoot? root;
        try
        {
            root = ReadUnifiedRootStreaming(unifiedPath, includeMaterialInterfaces: !lean,
                materialKeyFilter: filtered ? materialFilter : null);
        }
        catch (Exception ex)
        {
            state.LogError($"UnifiedShaderMetadata.json read failed: {ex.Message}");
            return;
        }
        if (root == null) return;

        if (!string.IsNullOrWhiteSpace(root.GameVersionEnum))
        {
            state.GameVersionEnum = root.GameVersionEnum!;
        }

        HashSet<string> filterVariants = MaterialPathVariants.BuildFilterSet(state.Options.MaterialFilter);

        if (root.PackageShaderMapHashes != null)
        {
            foreach (KeyValuePair<string, List<string>> kvp in root.PackageShaderMapHashes)
            {
                string materialPath = kvp.Key.Replace('\\', '/');
                if (!MatchesFilter(materialPath, filterVariants)) continue;
                if (kvp.Value == null) continue;
                foreach (string hash in kvp.Value)
                {
                    if (!string.IsNullOrWhiteSpace(hash)) AddHash(state.HashToMaterialsFromUnified, hash, materialPath);
                }
            }
        }

        if (root.MaterialResourceHashes != null)
        {
            foreach (KeyValuePair<string, List<string>> kvp in root.MaterialResourceHashes)
            {
                string hash = kvp.Key;
                if (string.IsNullOrWhiteSpace(hash) || kvp.Value == null) continue;
                foreach (string assetPath in kvp.Value)
                {
                    string normalized = assetPath.Replace('\\', '/');
                    if (!MatchesFilter(normalized, filterVariants)) continue;
                    AddHash(state.HashToMaterialsFromUnified, hash, normalized);
                }
            }
        }

        if (root.NiagaraShaderMapHashes != null)
        {
            foreach (KeyValuePair<string, List<string>> kvp in root.NiagaraShaderMapHashes)
            {
                string hash = kvp.Key;
                if (string.IsNullOrWhiteSpace(hash) || kvp.Value == null) continue;
                foreach (string assetPath in kvp.Value)
                {
                    string normalized = assetPath.Replace('\\', '/');
                    if (!MatchesFilter(normalized, filterVariants)) continue;
                    AddHash(state.HashToMaterialsFromUnified, hash, normalized);
                }
            }
        }

        if (root.MaterialInterfaces != null)
        {
            foreach (KeyValuePair<string, UnifiedMaterialEntry> kvp in root.MaterialInterfaces)
            {
                string materialPath = NormalizeMaterialPathKey(kvp.Key);
                if (!MatchesFilter(materialPath, filterVariants)) continue;

                UnifiedMaterialEntry? mat = kvp.Value;
                if (mat == null) continue;

                List<UnifiedShaderMapEntry>? shaderMaps = mat.LoadedShaderMaps;
                if (shaderMaps != null)
                {
                    foreach (UnifiedShaderMapEntry sm in shaderMaps)
                    {
                        if (!string.IsNullOrWhiteSpace(sm?.CookedShaderMapIdHash)) AddHash(state.HashToMaterialsFromUnified, sm!.CookedShaderMapIdHash!, materialPath);
                        if (!string.IsNullOrWhiteSpace(sm?.ShaderContentHash)) AddHash(state.HashToMaterialsFromUnified, sm!.ShaderContentHash!, materialPath);
                        if (!string.IsNullOrWhiteSpace(sm?.ResourceHash)) AddHash(state.HashToMaterialsFromUnified, sm!.ResourceHash!, materialPath);
                    }
                }

                List<string>? perMaterialHashes = mat.PackageShaderMapHashes;
                if (perMaterialHashes != null)
                {
                    foreach (string h in perMaterialHashes)
                    {
                        if (!string.IsNullOrWhiteSpace(h)) AddHash(state.HashToMaterialsFromUnified, h, materialPath);
                    }
                }
            }
        }

        state.Log($"    UnifiedShaderMetadata.json: hash-to-materials index size={state.HashToMaterialsFromUnified.Count}.");
    }

    private const long MaxFullReadBytes = 1024L * 1024 * 1024;
    private static UnifiedRoot ReadUnifiedRootStreaming(string path, bool includeMaterialInterfaces, string? materialKeyFilter = null)
    {
        var root = new UnifiedRoot();
        NewtonsoftJsonSerializer serializer = NewtonsoftJsonSerializer.CreateDefault();

        using FileStream stream = File.OpenRead(path);
        using var textReader = new StreamReader(stream);
        using var reader = new JsonTextReader(textReader);

        if (!reader.Read() || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject) return root;
        while (reader.Read() && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
        {
            string prop = (string)reader.Value!;
            if (!reader.Read()) break;
            switch (prop)
            {
                case nameof(UnifiedRoot.GameVersionEnum):
                    root.GameVersionEnum = reader.Value?.ToString();
                    break;
                case nameof(UnifiedRoot.PackageShaderMapHashes):
                    root.PackageShaderMapHashes = serializer.Deserialize<Dictionary<string, List<string>>>(reader);
                    break;
                case nameof(UnifiedRoot.NiagaraShaderMapHashes):
                    root.NiagaraShaderMapHashes = serializer.Deserialize<Dictionary<string, List<string>>>(reader);
                    break;
                case nameof(UnifiedRoot.MaterialResourceHashes):
                    root.MaterialResourceHashes = serializer.Deserialize<Dictionary<string, List<string>>>(reader);
                    break;
                case nameof(UnifiedRoot.MaterialInterfaces):
                    if (!includeMaterialInterfaces)
                    {
                        reader.Skip();
                    }
                    else if (materialKeyFilter == null)
                    {
                        root.MaterialInterfaces = serializer.Deserialize<Dictionary<string, UnifiedMaterialEntry>>(reader);
                    }
                    else
                    {
                        var kept = new Dictionary<string, UnifiedMaterialEntry>(StringComparer.OrdinalIgnoreCase);
                        if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject)
                        {
                            while (reader.Read() && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                            {
                                string key = (string)reader.Value!;
                                if (!reader.Read()) break;
                                if (key.IndexOf(materialKeyFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    UnifiedMaterialEntry? entry = serializer.Deserialize<UnifiedMaterialEntry>(reader);
                                    if (entry != null) kept[key] = entry;
                                }
                                else
                                {
                                    reader.Skip();
                                }
                            }
                        }
                        root.MaterialInterfaces = kept;
                    }
                    break;
                default:
                    reader.Skip();                    break;
            }
        }
        return root;
    }

    private static void AddHash(Dictionary<string, HashSet<string>> result, string hash, string materialPath)
    {
        if (!result.TryGetValue(hash, out HashSet<string>? materials))
        {
            materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result[hash] = materials;
        }
        materials.Add(materialPath);
    }

    private static bool MatchesFilter(string materialPath, HashSet<string> filterVariants)
        => MaterialPathVariants.Matches(materialPath, filterVariants);

    private static string NormalizeMaterialPathKey(string materialPath)
    {
        string normalized = materialPath.Replace('\\', '/');
        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        return dotIndex > slashIndex ? normalized[..dotIndex] : normalized;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class UnifiedRoot
    {
        public string? GameVersionEnum { get; set; }
        public Dictionary<string, List<string>>? PackageShaderMapHashes { get; set; }
        public Dictionary<string, UnifiedMaterialEntry>? MaterialInterfaces { get; set; }
        public Dictionary<string, List<string>>? NiagaraShaderMapHashes { get; set; }
        public Dictionary<string, List<string>>? MaterialResourceHashes { get; set; }
    }
    private sealed class UnifiedMaterialEntry
    {
        public string? MaterialPath { get; set; }
        public List<UnifiedShaderMapEntry>? LoadedShaderMaps { get; set; }
        public List<string>? PackageShaderMapHashes { get; set; }
    }
    private sealed class UnifiedShaderMapEntry
    {
        public string? ShaderPlatform { get; set; }
        public string? CookedShaderMapIdHash { get; set; }
        public string? ShaderContentHash { get; set; }
        public string? ResourceHash { get; set; }
    }
}

internal static class MaterialPathVariants
{
    public static HashSet<string> BuildFilterSet(string? filterValue)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(filterValue)) return result;

        foreach (string token in filterValue.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = token.Trim().Replace('\\', '/');
            if (trimmed.Length == 0) continue;
            result.UnionWith(Build(trimmed));
        }
        return result;
    }

    public static bool Matches(string? materialPath, HashSet<string> filterVariants)
    {
        if (filterVariants.Count == 0) return true;
        if (string.IsNullOrEmpty(materialPath)) return false;
        string normalized = materialPath!.Replace('\\', '/');
        if (Build(normalized).Overlaps(filterVariants)) return true;
        foreach (string token in filterVariants)
        {
            if (token.Length > 0 && normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static HashSet<string> Build(string? materialPath)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(materialPath)) return result;

        string normalized = materialPath!.Replace('\\', '/');
        result.Add(normalized);

        if (normalized.StartsWith("/", StringComparison.Ordinal)) result.Add(normalized[1..]);
        else result.Add("/" + normalized);

        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        if (dotIndex > slashIndex) result.Add(normalized[..dotIndex]);

        foreach (string current in result.ToArray())
        {
            int contentIdx = current.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIdx >= 0)
            {
                string trimmed = current[(contentIdx + "/Content/".Length)..];
                result.Add(trimmed);
                result.Add("/" + trimmed);
            }
            else if (current.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            {
                string trimmed = current["Content/".Length..];
                result.Add(trimmed);
                result.Add("/" + trimmed);
            }
        }

        return result;
    }
}
