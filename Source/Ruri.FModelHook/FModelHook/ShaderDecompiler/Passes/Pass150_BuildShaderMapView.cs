using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass150_BuildShaderMapView
{
    public static void DoPass(PipelineState state)
    {
        if (state.Library is null) throw new InvalidOperationException("Pass010 must run before Pass050.");

        AddSidecarUsage(state);
        AddUnifiedUsage(state);
        BuildView(state);

        state.Log($"    Asset index: usage entries={state.UsageByShaderIndex.Count}, named={state.NameByShaderIndex.Count}, shader-maps={state.ShaderMaps.Count}.");
    }

    private static void AddSidecarUsage(PipelineState state)
    {
        ShaderLibrary lib = state.Library!;

        int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count);
        for (int shaderMapIndex = 0; shaderMapIndex < mapCount; shaderMapIndex++)
        {
            if (!state.ShaderMapToAssets.TryGetValue(lib.ShaderMapHashes[shaderMapIndex], out HashSet<string>? assets) || assets.Count == 0) continue;

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
            if (!state.ShaderHashToAssetsByFreq.TryGetValue(shaderHash, out Dictionary<string, HashSet<byte>>? assetMap)) continue;

            foreach ((string asset, HashSet<byte> frequencies) in assetMap)
            {
                if (frequencies.Contains(frequency)) AddUsage(state, shaderIndex, asset);
            }
        }
    }

    private static void AddUnifiedUsage(PipelineState state)
    {
        if (state.HashToMaterialsFromUnified.Count == 0) return;

        ShaderLibrary lib = state.Library!;
        int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count);
        for (int shaderMapIndex = 0; shaderMapIndex < mapCount; shaderMapIndex++)
        {
            string hash = lib.ShaderMapHashes[shaderMapIndex];
            if (!state.HashToMaterialsFromUnified.TryGetValue(hash, out HashSet<string>? materials)) continue;

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

    private static void BuildView(PipelineState state)
    {
        ShaderLibrary lib = state.Library!;
        HashSet<string> filterVariants = MaterialPathVariants.BuildFilterSet(state.Options.MaterialFilter);

        int mapCount = Math.Min(lib.ShaderMapEntries.Length, lib.ShaderMapHashes.Count);
        for (int mapIndex = 0; mapIndex < mapCount; mapIndex++)
        {
            string mapHash = lib.ShaderMapHashes[mapIndex];
            ShaderMapEntry mapEntry = lib.ShaderMapEntries[mapIndex];
            List<string> assets = ResolveShaderMapAssets(state, mapHash);

            if (filterVariants.Count > 0)
            {
                bool matches = false;
                foreach (string token in filterVariants)
                {
                    if (token.Length >= 8
                        && (mapHash.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                            || token.StartsWith(mapHash, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches = true;
                        break;
                    }
                }
                if (!matches)
                {
                    foreach (string asset in assets)
                    {
                        if (MaterialPathVariants.Matches(asset, filterVariants))
                        {
                            matches = true;
                            break;
                        }
                    }
                }
                if (!matches) continue;
            }

            List<ShaderMapMember> members = new((int)mapEntry.NumShaders);
            for (uint i = 0; i < mapEntry.NumShaders; i++)
            {
                long offset = mapEntry.ShaderIndicesOffset + i;
                if (offset < 0 || offset >= lib.ShaderIndices.Length) continue;
                int shaderIndex = (int)lib.ShaderIndices[offset];
                if (shaderIndex < 0 || shaderIndex >= lib.ShaderEntries.Length) continue;

                members.Add(new ShaderMapMember
                {
                    RelativeIndex = (int)i,
                    ArchiveShaderIndex = shaderIndex,
                });
            }

            string primaryAsset = assets.Count > 0 ? assets[0] : string.Empty;
            string primaryName = string.IsNullOrEmpty(primaryAsset)
                ? "UnknownMaterial"
                : Path.GetFileNameWithoutExtension(primaryAsset);
            if (string.IsNullOrWhiteSpace(primaryName)) primaryName = "UnknownMaterial";

            state.ShaderMaps.Add(new ShaderMapInfo
            {
                ShaderMapIndex = mapIndex,
                ShaderMapHash = mapHash,
                Assets = assets,
                PrimaryAsset = primaryAsset,
                PrimaryName = primaryName,
                Members = members,
            });
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

    private static string ShortHash(string value, int length)
    {
        if (string.IsNullOrEmpty(value)) return "UNKNOWN";
        return value.Length <= length ? value : value[..length];
    }

    private static List<string> ResolveShaderMapAssets(PipelineState state, string mapHash)
    {
        if (state.ShaderMapToAssets.TryGetValue(mapHash, out HashSet<string>? assetSet) && assetSet.Count > 0)
        {
            return assetSet.OrderBy(static a => a, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (state.HashToMaterialsFromUnified.TryGetValue(mapHash, out HashSet<string>? materials) && materials.Count > 0)
        {
            return materials.OrderBy(static a => a, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return new List<string>();
    }
}
