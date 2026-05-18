using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 140 — Read `UnifiedShaderMetadata.json` and produce a
// `state.HashToMaterialsFromUnified[hash] = {materialPaths}` index.
//
// `UnifiedShaderMetadata.json` is the cross-library global written by
// Pass 080 on the export side. It carries THREE independent hash bridges,
// one per ID space the cook pipeline produces:
//
//   - `PackageShaderMapHashes`: per-package list of on-disk shader-map
//     hashes (the IoStore container header's StoreEntries[i].ShaderMapHashes).
//     This is the FMaterialShaderMap on-disk hash for IoStore cooks.
//
//   - `MaterialInterfaces[<x>].LoadedShaderMaps[*].CookedShaderMapIdHash`
//     and `.ShaderContentHash`: per-material identifiers UE uses
//     internally (NOT equal to the on-disk hash for IoStore cooks). Set
//     by Pass 030 when the inline shader-map blob survives shipping cook.
//
//   - `NiagaraShaderMapHashes[hash] = {assetPaths}`: the FShaderMapBase.ResourceHash
//     for Niagara compute / sprite GPU shaders. Populated by Pass 035 and
//     INDEPENDENT from the material side because Niagara uses its own
//     `FNiagaraShaderMapId` derivation (CompilerVersion + DI type set +
//     script hash). Without this bridge, Niagara-only archives like
//     X6Game_10_2537 (101/101 UnknownMaterial without it) have zero
//     material-side overlap and every shader resolves anonymously.
//
// All three are folded into the same (hash -> assets) lookup so the
// downstream Pass 150 doesn't need to know which bridge an entry came
// from. Pass 050 uses it as the bridge from on-disk shader-map hash
// to material when the asset-info sidecar already has the answer;
// later passes use it for fallback lookups when the per-library sidecar
// misses.
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

        // Capture the game-version enum so Pass 145 can drive a
        // game-aware EngineUbMetadata folder selection.
        if (!string.IsNullOrWhiteSpace(root.GameVersionEnum))
        {
            state.GameVersionEnum = root.GameVersionEnum!;
        }

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

        // Niagara bridge — keyed BY HASH (not by package), so the value
        // direction is reversed from PackageShaderMapHashes. Pass 035
        // built this from FShaderMapBase.ResourceHash. The hash is the
        // SAME on-disk hash format the .ushaderbytecode archive uses, so
        // a single AddHash per (hash, asset) pair plugs straight into
        // the existing lookup with no special casing downstream.
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

                // Bridge 1: hashes the inline shader-map carries (older /
                // non-IoStore cooks).
                List<UnifiedShaderMapEntry>? shaderMaps = mat.LoadedShaderMaps;
                if (shaderMaps != null)
                {
                    foreach (UnifiedShaderMapEntry sm in shaderMaps)
                    {
                        if (!string.IsNullOrWhiteSpace(sm?.CookedShaderMapIdHash)) AddHash(state.HashToMaterialsFromUnified, sm!.CookedShaderMapIdHash!, materialPath);
                        if (!string.IsNullOrWhiteSpace(sm?.ShaderContentHash)) AddHash(state.HashToMaterialsFromUnified, sm!.ShaderContentHash!, materialPath);
                    }
                }

                // Bridge 2: per-material PackageShaderMapHashes copy
                // (Pass020 mirrors the IoStore container header's
                // shader-map-hash list onto every UMaterialInterface entry
                // it scans). This is the AUTHORITATIVE bridge for modern
                // UE5 IoStore cooks where the inline shader-map blob is
                // empty — without it, every shader produced by an
                // externalised shader-archive falls back to "UnknownMaterial"
                // even though the material UAsset is right there.
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
        // FModel EGame enum name (e.g. "GAME_UE5_1", "GAME_InfinityNikki")
        // captured at export. Used to pick the right
        // EngineUbMetadata/<EGame>/ subfolder so game-specific UE forks
        // get game-specific layouts.
        public string? GameVersionEnum { get; set; }
        public Dictionary<string, List<string>>? PackageShaderMapHashes { get; set; }
        public Dictionary<string, UnifiedMaterialEntry>? MaterialInterfaces { get; set; }
        // Niagara-side independent bridge written by Pass 035. Keyed
        // BY HASH (FShaderMapBase.ResourceHash) — value is the list of
        // Niagara asset paths whose `LoadedScriptResources[*].
        // RenderingThreadShaderMap` produced that hash.
        public Dictionary<string, List<string>>? NiagaraShaderMapHashes { get; set; }
    }
    private sealed class UnifiedMaterialEntry
    {
        public string? MaterialPath { get; set; }
        public List<UnifiedShaderMapEntry>? LoadedShaderMaps { get; set; }
        // Mirror of the IoStore container header's shader-map-hash list
        // for THIS material's package. Pass020 fills it from
        // `state.Root.PackageShaderMapHashes[<package>]` so the unified
        // file carries an authoritative bridge even when the inline
        // shader map is gone.
        public List<string>? PackageShaderMapHashes { get; set; }
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
