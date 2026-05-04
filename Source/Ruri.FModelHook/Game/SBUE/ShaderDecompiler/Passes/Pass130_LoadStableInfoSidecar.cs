using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 030 — Read the per-library `.stableinfo.json` sidecar.
//
// `.stableinfo.json` is the per-shader-map breakdown that holds
// type/VF/permutation truth recovered by Pass 110 from the cooked
// material's UniformExpressionSet + TypeDependencies. It populates
// THREE state slots, all keyed for downstream lookups:
//
//   1. `state.ShaderHashToAssetsByFreq[shaderHash][asset] = {freq...}`
//      — the hash-level usage fan-out used as a fallback for shaders
//      whose owning shader-map didn't list them.
//
//   2. `state.ContainerByShaderIndex[archiveIndex] = ShaderContainerInfo`
//      — last-write-wins global view, used as a fallback when callers
//      don't know which shader-map a binary belongs to.
//
//   3. `state.ContainersByMapAndIndex[mapHash][archiveIndex] = ...`
//      — authoritative per-map view; the SAME shader binary can show up
//      under different ShaderType/VF in different maps, and emission
//      must read the OWNING-map's truth.
//
// File holds nothing else — DTO shapes are private to keep the JSON
// schema localised to the pass that owns the read.
internal static class Pass130_LoadStableInfoSidecar
{
    public static void DoPass(PipelineState state)
    {
        string sidecarPath = SidecarBasePath(state.Options.LibraryPath) + ".stableinfo.json";
        if (!File.Exists(sidecarPath))
        {
            state.Log("    .stableinfo.json: missing.");
            return;
        }

        StableInfoRoot? root = JsonSerializer.Deserialize<StableInfoRoot>(File.ReadAllText(sidecarPath), JsonOptions);
        if (root?.ShaderMaps == null) return;

        foreach (StableInfoEntry shaderMap in root.ShaderMaps)
        {
            // Merge stableinfo's per-map asset list into the shared
            // ShaderMapToAssets index. assetinfo is the primary source, but
            // stableinfo carries the same association and can rescue maps the
            // assetinfo sidecar missed.
            if (!string.IsNullOrWhiteSpace(shaderMap.ShaderMapHash) && shaderMap.Assets != null)
            {
                if (!state.ShaderMapToAssets.TryGetValue(shaderMap.ShaderMapHash!, out HashSet<string>? assets))
                {
                    assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    state.ShaderMapToAssets[shaderMap.ShaderMapHash!] = assets;
                }

                foreach (string asset in shaderMap.Assets.Where(static a => !string.IsNullOrWhiteSpace(a)))
                {
                    assets.Add(asset.Replace('\\', '/'));
                }
            }

            // Slot 1: hash-level fan-out.
            if (shaderMap.ShaderHashes != null && shaderMap.Frequencies != null && shaderMap.Assets != null)
            {
                int count = Math.Min(shaderMap.ShaderHashes.Count, shaderMap.Frequencies.Count);
                for (int i = 0; i < count; i++)
                {
                    string shaderHash = shaderMap.ShaderHashes[i];
                    if (string.IsNullOrWhiteSpace(shaderHash)) continue;

                    if (!state.ShaderHashToAssetsByFreq.TryGetValue(shaderHash, out Dictionary<string, HashSet<byte>>? assetMap))
                    {
                        assetMap = new Dictionary<string, HashSet<byte>>(StringComparer.OrdinalIgnoreCase);
                        state.ShaderHashToAssetsByFreq[shaderHash] = assetMap;
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

            // Slots 2 & 3: per-shader container info.
            if (shaderMap.Shaders == null || shaderMap.Shaders.Count == 0) continue;

            string firstAsset = shaderMap.Assets?.Where(static a => !string.IsNullOrWhiteSpace(a))
                .Select(static a => a.Replace('\\', '/'))
                .OrderBy(static a => a, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? "UnknownMaterial";
            string materialName = Path.GetFileNameWithoutExtension(firstAsset);
            if (string.IsNullOrWhiteSpace(materialName)) materialName = "UnknownMaterial";

            // Per-map slot — every map has its own dict regardless of
            // whether other maps share its binaries.
            Dictionary<int, ShaderContainerInfo>? perMap = null;
            if (!string.IsNullOrWhiteSpace(shaderMap.ShaderMapHash))
            {
                if (!state.ContainersByMapAndIndex.TryGetValue(shaderMap.ShaderMapHash!, out perMap))
                {
                    perMap = new Dictionary<int, ShaderContainerInfo>();
                    state.ContainersByMapAndIndex[shaderMap.ShaderMapHash!] = perMap;
                }
            }

            foreach (StableShaderSidecarEntry entry in shaderMap.Shaders)
            {
                if (entry.ArchiveShaderIndex < 0) continue;
                ShaderContainerInfo info = BuildContainerInfo(shaderMap, entry, materialName);

                // Slot 2: global last-write-wins.
                state.ContainerByShaderIndex[entry.ArchiveShaderIndex] = info;
                // Slot 3: per-map authoritative.
                if (perMap != null) perMap[entry.ArchiveShaderIndex] = info;
            }
        }

        state.Log($"    .stableinfo.json: hash-fanout={state.ShaderHashToAssetsByFreq.Count} containers={state.ContainerByShaderIndex.Count} per-map={state.ContainersByMapAndIndex.Count}.");
    }

    private static ShaderContainerInfo BuildContainerInfo(StableInfoEntry shaderMap, StableShaderSidecarEntry entry, string materialName)
    {
        return new ShaderContainerInfo
        {
            ContainerKey = string.IsNullOrWhiteSpace(entry.ContainerKey)
                ? BuildContainerKey(shaderMap.ShaderMapHash, entry.ShaderTypeHash, entry.VertexFactoryTypeHash)
                : entry.ContainerKey,
            MaterialName = materialName,
            ShaderMapHash = shaderMap.ShaderMapHash ?? string.Empty,
            ShaderTypeHash = entry.ShaderTypeHash ?? string.Empty,
            ShaderTypeName = entry.ShaderTypeName ?? string.Empty,
            VertexFactoryTypeHash = entry.VertexFactoryTypeHash ?? string.Empty,
            VertexFactoryTypeName = entry.VertexFactoryTypeName ?? string.Empty,
            PipelineTypeHash = entry.PipelineTypeHash ?? string.Empty,
            PipelineTypeName = entry.PipelineTypeName ?? string.Empty,
            PermutationId = entry.PermutationId,
            ResourceIndex = entry.ResourceIndex,
            Frequency = entry.Frequency,
            ShaderHash = entry.ShaderHash ?? string.Empty
        };
    }

    private static string BuildContainerKey(string? shaderMapHash, string? shaderTypeHash, string? vertexFactoryTypeHash)
    {
        string mapPart = ShortHash(shaderMapHash, 12);
        string typePart = ShortHash(shaderTypeHash, 16);
        string vfPart = string.IsNullOrWhiteSpace(vertexFactoryTypeHash) ? "NOVF" : ShortHash(vertexFactoryTypeHash, 16);
        return $"SM{mapPart}_T{typePart}_VF{vfPart}";
    }

    private static string ShortHash(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return "UNKNOWN";
        string normalized = value!.Trim();
        return normalized.Length <= length ? normalized : normalized[..length];
    }

    private static string SidecarBasePath(string libraryPath)
    {
        const string suffix = ".ushaderlib";
        return libraryPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? libraryPath[..^suffix.Length]
            : libraryPath;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class StableInfoRoot { public List<StableInfoEntry>? ShaderMaps { get; set; } }
    private sealed class StableInfoEntry
    {
        public string? ShaderMapHash { get; set; }
        public List<string>? Assets { get; set; }
        public List<string>? ShaderHashes { get; set; }
        public List<byte>? Frequencies { get; set; }
        public List<StableShaderSidecarEntry>? Shaders { get; set; }
    }

    private sealed class StableShaderSidecarEntry
    {
        public int ArchiveShaderIndex { get; set; }
        public int ResourceIndex { get; set; }
        public string? ShaderHash { get; set; }
        public byte Frequency { get; set; }
        public string? ShaderTypeHash { get; set; }
        public string? ShaderTypeName { get; set; }
        public string? VertexFactoryTypeHash { get; set; }
        public string? VertexFactoryTypeName { get; set; }
        public string? PipelineTypeHash { get; set; }
        public string? PipelineTypeName { get; set; }
        public int PermutationId { get; set; }
        public string? ContainerKey { get; set; }
    }
}
