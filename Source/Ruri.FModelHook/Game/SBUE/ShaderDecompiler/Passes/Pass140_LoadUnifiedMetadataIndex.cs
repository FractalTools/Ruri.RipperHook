using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 040 — Read `UnifiedShaderMetadata.json` and produce a
// `state.HashToMaterialsFromUnified[hash] = {materialPaths}` index.
//
// `UnifiedShaderMetadata.json` is the cross-library global written by
// Pass 110. It has TWO independent hash sources for the same materials:
//
//   - `PackageShaderMapHashes`: per-package list of on-disk shader-map
//     hashes (the IoStore container header's StoreEntries[i].ShaderMapHashes).
//
//   - `MaterialInterfaces[<x>].LoadedShaderMaps[*].CookedShaderMapIdHash`
//     and `.ShaderContentHash`: per-material identifiers UE uses
//     internally (NOT equal to the on-disk hash for IoStore cooks).
//
// Both are folded into one (hash -> materials) lookup. Pass 050 uses
// it as the bridge from on-disk shader-map hash to material when the
// asset-info sidecar already has the answer; later passes use it for
// fallback lookups when the per-library sidecar misses.
//
// Material filter (`state.Options.MaterialFilter`) is applied here so
// downstream slots stay scoped to the user's request.
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

        UnifiedRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<UnifiedRoot>(File.ReadAllText(unifiedPath), JsonOptions);
        }
        catch (Exception ex)
        {
            state.LogError($"UnifiedShaderMetadata.json read failed: {ex.Message}");
            return;
        }
        if (root == null) return;

        string? normalizedFilter = string.IsNullOrWhiteSpace(state.Options.MaterialFilter)
            ? null
            : state.Options.MaterialFilter!.Replace('\\', '/');
        HashSet<string> filterVariants = MaterialPathVariants.Build(normalizedFilter);

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
                    if (!string.IsNullOrWhiteSpace(sm?.CookedShaderMapIdHash)) AddHash(state.HashToMaterialsFromUnified, sm!.CookedShaderMapIdHash!, materialPath);
                    if (!string.IsNullOrWhiteSpace(sm?.ShaderContentHash)) AddHash(state.HashToMaterialsFromUnified, sm!.ShaderContentHash!, materialPath);
                }
            }
        }

        state.Log($"    UnifiedShaderMetadata.json: hash-to-materials index size={state.HashToMaterialsFromUnified.Count}.");
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
// any of those forms with a single `HashSet.Overlaps` call. Lives at
// file scope (not inside any pass) because both the metadata-index
// pass and the symbol-source readers (Pass 060) need it.
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
