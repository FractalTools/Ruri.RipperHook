using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 001 — Build per-shader (usage map + name map) by merging two
// truth sources. Both walks share `state.Library`'s ShaderMapEntries /
// ShaderIndices, so they're fused into one pass — splitting would
// double-walk those arrays. Sources:
//
//   1. `<library>.assetinfo.json` / `.stableinfo.json` (per-library
//      sidecars, written by UnifiedShaderMetadataExporter). Always
//      consulted first; provides shader-map-hash → assets[].
//
//   2. `UnifiedShaderMetadata.json`. May be absent (first library
//      before the unified-export ran). Provides BOTH per-hash assets
//      AND a display name derived from the first material per
//      shader-map (which is what the HLSL filename uses).
internal static class Pass001_BuildAssetIndex
{
    public static void DoPass(PipelineState state)
    {
        if (state.Library is null) throw new InvalidOperationException("Pass000 must run before Pass001.");

        string? normalizedFilter = string.IsNullOrWhiteSpace(state.Options.MaterialFilter)
            ? null
            : state.Options.MaterialFilter!.Replace('\\', '/');

        Dictionary<string, HashSet<string>> shaderMapToAssets = LoadAssetInfoSidecar(state.Options.LibraryPath + ".assetinfo.json");
        Dictionary<string, Dictionary<string, HashSet<byte>>> shaderHashToAssetsByFreq = LoadStableInfoSidecar(state.Options.LibraryPath + ".stableinfo.json");
        AddSidecarUsage(state, shaderMapToAssets, shaderHashToAssetsByFreq);

        if (!string.IsNullOrEmpty(state.Options.UnifiedMetadataPath) && File.Exists(state.Options.UnifiedMetadataPath))
        {
            UnifiedRoot? unified = LoadUnifiedRoot(state.Options.UnifiedMetadataPath);
            if (unified != null)
            {
                Dictionary<string, HashSet<string>> hashToMaterials = BuildHashToMaterialsMap(unified, normalizedFilter);
                AddUnifiedUsage(state, hashToMaterials);
            }
        }

        state.Log($"    Asset index: usage entries={state.UsageByShaderIndex.Count}, named={state.NameByShaderIndex.Count}.");
    }

    // ------------- Sidecar fan-in -------------

    private static void AddSidecarUsage(
        PipelineState state,
        Dictionary<string, HashSet<string>> shaderMapToAssets,
        Dictionary<string, Dictionary<string, HashSet<byte>>> shaderHashToAssetsByFreq)
    {
        ShaderLibrary lib = state.Library!;
        int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count);
        for (int shaderMapIndex = 0; shaderMapIndex < mapCount; shaderMapIndex++)
        {
            if (!shaderMapToAssets.TryGetValue(lib.ShaderMapHashes[shaderMapIndex], out HashSet<string>? assets) || assets.Count == 0) continue;

            ShaderMapEntry mapEntry = lib.ShaderMapEntries[shaderMapIndex];
            for (uint i = 0; i < mapEntry.NumShaders; i++)
            {
                long offset = mapEntry.ShaderIndicesOffset + i;
                if (offset < 0 || offset >= lib.ShaderIndices.Length) continue;

                int shaderIndex = (int)lib.ShaderIndices[offset];
                foreach (string asset in assets) AddUsage(state, shaderIndex, asset);
            }
        }

        for (int shaderIndex = 0; shaderIndex < lib.ShaderHashes.Count && shaderIndex < lib.ShaderEntries.Length; shaderIndex++)
        {
            string shaderHash = lib.ShaderHashes[shaderIndex];
            byte frequency = lib.ShaderEntries[shaderIndex].Frequency;
            if (!shaderHashToAssetsByFreq.TryGetValue(shaderHash, out Dictionary<string, HashSet<byte>>? assetMap)) continue;

            foreach ((string asset, HashSet<byte> frequencies) in assetMap)
            {
                if (frequencies.Contains(frequency)) AddUsage(state, shaderIndex, asset);
            }
        }
    }

    private static void AddUnifiedUsage(PipelineState state, Dictionary<string, HashSet<string>> hashToMaterials)
    {
        ShaderLibrary lib = state.Library!;
        int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count);
        for (int shaderMapIndex = 0; shaderMapIndex < mapCount; shaderMapIndex++)
        {
            string hash = lib.ShaderMapHashes[shaderMapIndex];
            if (!hashToMaterials.TryGetValue(hash, out HashSet<string>? materials)) continue;

            ShaderMapEntry mapEntry = lib.ShaderMapEntries[shaderMapIndex];
            string representative = materials.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "Unknown";
            string displayName = Path.GetFileNameWithoutExtension(representative);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "UnknownMaterial";

            for (uint i = 0; i < mapEntry.NumShaders; i++)
            {
                long offset = mapEntry.ShaderIndicesOffset + i;
                if (offset < 0 || offset >= lib.ShaderIndices.Length) continue;

                int shaderIndex = (int)lib.ShaderIndices[offset];
                foreach (string material in materials) AddUsage(state, shaderIndex, material);
                state.NameByShaderIndex.TryAdd(shaderIndex, displayName);
            }
        }
    }

    private static void AddUsage(PipelineState state, int shaderIndex, string material)
    {
        if (!state.UsageByShaderIndex.TryGetValue(shaderIndex, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            state.UsageByShaderIndex[shaderIndex] = set;
        }
        set.Add(material);
    }

    // ------------- Sidecar JSON readers -------------

    private static Dictionary<string, HashSet<string>> LoadAssetInfoSidecar(string path)
    {
        Dictionary<string, HashSet<string>> result = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        AssetInfoRoot? root = JsonSerializer.Deserialize<AssetInfoRoot>(File.ReadAllText(path), JsonOptions);
        if (root?.ShaderCodeToAssets == null) return result;

        foreach (AssetInfoEntry entry in root.ShaderCodeToAssets)
        {
            if (string.IsNullOrWhiteSpace(entry.ShaderMapHash) || entry.Assets == null) continue;
            if (!result.TryGetValue(entry.ShaderMapHash, out HashSet<string>? assets))
            {
                assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[entry.ShaderMapHash] = assets;
            }
            foreach (string asset in entry.Assets.Where(static a => !string.IsNullOrWhiteSpace(a)))
            {
                assets.Add(asset.Replace('\\', '/'));
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, HashSet<byte>>> LoadStableInfoSidecar(string path)
    {
        Dictionary<string, Dictionary<string, HashSet<byte>>> result = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;

        StableInfoRoot? root = JsonSerializer.Deserialize<StableInfoRoot>(File.ReadAllText(path), JsonOptions);
        if (root?.ShaderMaps == null) return result;

        foreach (StableInfoEntry shaderMap in root.ShaderMaps)
        {
            if (shaderMap.ShaderHashes == null || shaderMap.Frequencies == null || shaderMap.Assets == null) continue;
            int count = Math.Min(shaderMap.ShaderHashes.Count, shaderMap.Frequencies.Count);
            for (int i = 0; i < count; i++)
            {
                string shaderHash = shaderMap.ShaderHashes[i];
                if (string.IsNullOrWhiteSpace(shaderHash)) continue;

                if (!result.TryGetValue(shaderHash, out Dictionary<string, HashSet<byte>>? assetMap))
                {
                    assetMap = new Dictionary<string, HashSet<byte>>(StringComparer.OrdinalIgnoreCase);
                    result[shaderHash] = assetMap;
                }
                foreach (string asset in shaderMap.Assets.Where(static a => !string.IsNullOrWhiteSpace(a)))
                {
                    string normalized = asset.Replace('\\', '/');
                    if (!assetMap.TryGetValue(normalized, out HashSet<byte>? frequencies))
                    {
                        frequencies = new HashSet<byte>();
                        assetMap[normalized] = frequencies;
                    }
                    frequencies.Add(shaderMap.Frequencies[i]);
                }
            }
        }
        return result;
    }

    // ------------- Unified metadata reader -------------

    private static UnifiedRoot? LoadUnifiedRoot(string jsonPath)
    {
        try
        {
            return JsonSerializer.Deserialize<UnifiedRoot>(File.ReadAllText(jsonPath), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // Combines BOTH unified-metadata sources into a single hash → materials
    // map: package-level shader-map hashes (IoStore container) AND
    // material-level CookedShaderMapIdHash + ShaderContentHash.
    private static Dictionary<string, HashSet<string>> BuildHashToMaterialsMap(UnifiedRoot root, string? normalizedMaterialFilter)
    {
        Dictionary<string, HashSet<string>> result = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> filterVariants = MaterialPathVariants.Build(normalizedMaterialFilter);

        if (root.PackageShaderMapHashes != null)
        {
            foreach (KeyValuePair<string, List<string>> kvp in root.PackageShaderMapHashes)
            {
                string materialPath = kvp.Key.Replace('\\', '/');
                if (!MatchesFilter(materialPath, filterVariants)) continue;
                if (kvp.Value == null) continue;

                foreach (string hash in kvp.Value)
                {
                    if (!string.IsNullOrWhiteSpace(hash)) AddHash(result, hash, materialPath);
                }
            }
        }

        if (root.MaterialInterfaces != null)
        {
            foreach (KeyValuePair<string, UnifiedMaterialEntry> kvp in root.MaterialInterfaces)
            {
                string materialPath = NormalizeMaterialPathKey(kvp.Key);
                if (!MatchesFilter(materialPath, filterVariants)) continue;

                List<UnifiedShaderMapEntry>? shaderMaps = kvp.Value?.LoadedShaderMaps;
                if (shaderMaps == null) continue;

                foreach (UnifiedShaderMapEntry sm in shaderMaps)
                {
                    if (!string.IsNullOrWhiteSpace(sm?.CookedShaderMapIdHash)) AddHash(result, sm!.CookedShaderMapIdHash!, materialPath);
                    if (!string.IsNullOrWhiteSpace(sm?.ShaderContentHash)) AddHash(result, sm!.ShaderContentHash!, materialPath);
                }
            }
        }

        return result;
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
        => filterVariants.Count == 0 || MaterialPathVariants.Build(materialPath).Overlaps(filterVariants);

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

    // Sidecar JSON shapes
    private sealed class AssetInfoRoot { public List<AssetInfoEntry>? ShaderCodeToAssets { get; set; } }
    private sealed class AssetInfoEntry { public string? ShaderMapHash { get; set; } public List<string>? Assets { get; set; } }
    private sealed class StableInfoRoot { public List<StableInfoEntry>? ShaderMaps { get; set; } }
    private sealed class StableInfoEntry
    {
        public string? ShaderMapHash { get; set; }
        public List<string>? Assets { get; set; }
        public List<string>? ShaderHashes { get; set; }
        public List<byte>? Frequencies { get; set; }
    }

    // Unified metadata JSON shapes (only the fields Pass001 needs).
    private sealed class UnifiedRoot
    {
        public Dictionary<string, List<string>>? PackageShaderMapHashes { get; set; }
        public Dictionary<string, UnifiedMaterialEntry>? MaterialInterfaces { get; set; }
    }

    private sealed class UnifiedMaterialEntry
    {
        public string? MaterialPath { get; set; }
        public List<UnifiedShaderMapEntry>? LoadedShaderMaps { get; set; }
    }

    private sealed class UnifiedShaderMapEntry
    {
        public string? ShaderPlatform { get; set; }
        public string? CookedShaderMapIdHash { get; set; }
        public string? ShaderContentHash { get; set; }
    }
}

// Path-spelling variant builder. UE export pipelines spell material
// paths inconsistently — with/without leading `/`, with/without the
// leading game-name segment, with/without the `.MaterialName` object
// suffix. Building a variant set per path lets callers match across
// any of those forms with a single HashSet.Overlaps call. Used by
// Pass001 (material filter) and Pass002 (material lookup).
internal static class MaterialPathVariants
{
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
