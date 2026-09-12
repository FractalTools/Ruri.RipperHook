using System;
using System.Collections.Generic;
using System.Linq;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass050_BuildStableShaderRecords
{
    private static readonly bool DebugTrace = string.Equals(Environment.GetEnvironmentVariable("RURI_TRUTH_DEBUG"), "1", StringComparison.Ordinal);

    public static void DoPass(ExportPipelineState state)
    {
        state.AssetInfo = null;
        state.StableInfo = null;

        var output = state.Root;
        if (state.Entry == null) return;
        if (!output.ShaderCodeArchives.TryGetValue(state.Entry.PathWithoutExtension, out var library))
        {
            return;
        }

        Dictionary<string, HashSet<string>> hashToMaterials = BuildHashToMaterialsMap(output);

        var assetInfo = new ShaderAssetInfoEquivalent();
        var stableInfo = new ShaderStableInfoEquivalent
        {
            LibraryPath = library.LibraryPath,
            LibraryName = library.LibraryName,
            LibraryType = library.LibraryType
        };

        int linked = 0;
        int unlinked = 0;
        foreach (string shaderMapHash in library.ShaderMapHashes)
        {
            List<string> assets = hashToMaterials.TryGetValue(shaderMapHash, out HashSet<string>? mats)
                ? mats.OrderBy(static m => m, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            if (assets.Count == 0)
            {
                unlinked++;
                continue;
            }
            linked++;

            assetInfo.ShaderCodeToAssets.Add(new ShaderAssetInfoEntry
            {
                ShaderMapHash = shaderMapHash,
                Assets = assets
            });

            int shaderMapIndex = library.ShaderMapHashes.FindIndex(hash => string.Equals(hash, shaderMapHash, StringComparison.OrdinalIgnoreCase));
            if (shaderMapIndex >= 0 && shaderMapIndex < library.ShaderMapEntries.Count)
            {
                var mapEntry = library.ShaderMapEntries[shaderMapIndex];
                var shaderHashes = new List<string>();
                var frequencies = new List<byte>();
                string shaderPlatform = ExtractShaderPlatform(library.LibraryName);
                List<StableShaderRecord> shaderRecords = BuildStableShaderRecords(output, library, shaderMapHash, mapEntry, hashToMaterials, shaderPlatform);

                for (uint i = 0; i < mapEntry.NumShaders; i++)
                {
                    long indexOffset = mapEntry.ShaderIndicesOffset + i;
                    if (indexOffset < 0 || indexOffset >= library.ShaderIndices.Count)
                    {
                        continue;
                    }

                    uint shaderIndex = library.ShaderIndices[(int)indexOffset];
                    if (shaderIndex >= library.ShaderHashes.Count || shaderIndex >= library.ShaderEntries.Count)
                    {
                        continue;
                    }

                    shaderHashes.Add(library.ShaderHashes[(int)shaderIndex]);
                    frequencies.Add(library.ShaderEntries[(int)shaderIndex].Frequency);
                }

                stableInfo.ShaderMaps.Add(new ShaderStableInfoEntry
                {
                    ShaderMapHash = shaderMapHash,
                    Assets = assets,
                    ShaderHashes = shaderHashes,
                    Frequencies = frequencies,
                    Shaders = shaderRecords,
                    Types = new List<string>(),
                    VertexFactoryTypes = new List<string>(),
                    ShaderTypeHashes = new List<string>(),
                    UniformBufferParameterStructHashes = new List<string>()
                });
            }
        }

        state.AssetInfo = assetInfo;
        state.StableInfo = stableInfo;
        HookLogger.Log($"[Pass050_BuildStableShaderRecords] {state.Entry.NameWithoutExtension}: linked={linked} shader-maps, unlinked={unlinked}.");
    }

    private static Dictionary<string, HashSet<string>> BuildHashToMaterialsMap(UnifiedShaderMetadataRoot output)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in output.PackageShaderMapHashes)
        {
            if (kvp.Value == null) continue;
            foreach (string hash in kvp.Value)
            {
                if (string.IsNullOrWhiteSpace(hash)) continue;
                AddMaterialToHash(map, hash, kvp.Key);
            }
        }

        foreach (var kvp in output.MaterialResourceHashes)
        {
            if (kvp.Value == null) continue;
            foreach (string materialPath in kvp.Value)
            {
                if (string.IsNullOrWhiteSpace(materialPath)) continue;
                AddMaterialToHash(map, kvp.Key, materialPath);
            }
        }

        foreach (var kvp in output.MaterialInterfaces)
        {
            if (kvp.Value?.LoadedShaderMaps == null) continue;
            foreach (var sm in kvp.Value.LoadedShaderMaps)
            {
                if (!string.IsNullOrWhiteSpace(sm?.ResourceHash))
                {
                    AddMaterialToHash(map, sm!.ResourceHash!, kvp.Key);
                }
                if (!string.IsNullOrWhiteSpace(sm?.CookedShaderMapIdHash))
                {
                    AddMaterialToHash(map, sm!.CookedShaderMapIdHash!, kvp.Key);
                }
                if (!string.IsNullOrWhiteSpace(sm?.ShaderContentHash))
                {
                    AddMaterialToHash(map, sm!.ShaderContentHash!, kvp.Key);
                }
            }
        }

        return map;
    }

    private static string ExtractShaderPlatform(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) return string.Empty;
        int firstDash = libraryName.IndexOf('-');
        if (firstDash < 0) return string.Empty;
        int secondDash = libraryName.IndexOf('-', firstDash + 1);
        if (secondDash < 0) return string.Empty;
        int thirdDash = libraryName.IndexOf('-', secondDash + 1);
        string plat = thirdDash >= 0
            ? libraryName.Substring(secondDash + 1, thirdDash - secondDash - 1)
            : libraryName.Substring(secondDash + 1);
        return string.IsNullOrWhiteSpace(plat) ? string.Empty : "SP_" + plat;
    }

    private static List<StableShaderRecord> BuildStableShaderRecords(
        UnifiedShaderMetadataRoot output,
        UnifiedShaderLibraryMetadata library,
        string shaderMapHash,
        UnifiedShaderMapArchiveEntry mapEntry,
        Dictionary<string, HashSet<string>> hashToMaterials,
        string shaderPlatform)
    {
        var result = new List<StableShaderRecord>();
        Dictionary<int, List<StableShaderRecord>> truthByResourceIndex = BuildTruthByResourceIndex(output, shaderMapHash, hashToMaterials, shaderPlatform);
        List<StableShaderRecord> orderedTruth = BuildOrderedTruthRecords(output, shaderMapHash, hashToMaterials, shaderPlatform);

        for (uint i = 0; i < mapEntry.NumShaders; i++)
        {
            long indexOffset = mapEntry.ShaderIndicesOffset + i;
            if (indexOffset < 0 || indexOffset >= library.ShaderIndices.Count)
            {
                continue;
            }

            uint shaderIndex = library.ShaderIndices[(int)indexOffset];
            if (shaderIndex >= library.ShaderHashes.Count || shaderIndex >= library.ShaderEntries.Count)
            {
                continue;
            }

            StableShaderRecord? truth = null;
            if (truthByResourceIndex.TryGetValue((int)i, out List<StableShaderRecord>? exactMatches) && exactMatches.Count > 0)
            {
                truth = exactMatches[0];
            }
            else if (i < orderedTruth.Count)
            {
                truth = orderedTruth[(int)i];
            }

            string shaderTypeHash = truth?.ShaderTypeHash ?? string.Empty;
            string shaderTypeName = truth?.ShaderTypeName ?? string.Empty;
            string vfHash = truth?.VertexFactoryTypeHash ?? string.Empty;
            string vfName = truth?.VertexFactoryTypeName ?? string.Empty;
            string pipelineHash = truth?.PipelineTypeHash ?? string.Empty;
            string pipelineName = truth?.PipelineTypeName ?? string.Empty;
            byte frequency = library.ShaderEntries[(int)shaderIndex].Frequency;

            result.Add(new StableShaderRecord
            {
                ArchiveShaderIndex = (int)shaderIndex,
                ResourceIndex = truth?.ResourceIndex ?? (int)i,
                ShaderHash = library.ShaderHashes[(int)shaderIndex],
                Frequency = frequency,
                ShaderTypeHash = shaderTypeHash,
                ShaderTypeName = shaderTypeName,
                VertexFactoryTypeHash = vfHash,
                VertexFactoryTypeName = vfName,
                PermutationId = truth?.PermutationId ?? -1,
                PipelineTypeHash = pipelineHash,
                PipelineTypeName = pipelineName,
                ContainerKey = BuildContainerKey(shaderMapHash, shaderTypeHash, vfHash)
            });
        }

        return result;
    }

    private static Dictionary<int, List<StableShaderRecord>> BuildTruthByResourceIndex(
        UnifiedShaderMetadataRoot output,
        string shaderMapHash,
        Dictionary<string, HashSet<string>> hashToMaterials,
        string shaderPlatform)
    {
        var result = new Dictionary<int, List<StableShaderRecord>>();
        foreach (StableShaderRecord record in BuildOrderedTruthRecords(output, shaderMapHash, hashToMaterials, shaderPlatform))
        {
            if (!result.TryGetValue(record.ResourceIndex, out List<StableShaderRecord>? list))
            {
                list = new List<StableShaderRecord>();
                result[record.ResourceIndex] = list;
            }
            list.Add(record);
        }
        return result;
    }

    private static List<StableShaderRecord> BuildOrderedTruthRecords(
        UnifiedShaderMetadataRoot output,
        string shaderMapHash,
        Dictionary<string, HashSet<string>> hashToMaterials,
        string shaderPlatform)
    {
        var result = new List<StableShaderRecord>();

        foreach (UnifiedMaterialMetadata material in output.MaterialInterfaces.Values)
        {
            if (material?.LoadedShaderMaps == null) continue;
            foreach (UnifiedShaderMapMetadata shaderMap in material.LoadedShaderMaps)
            {
                if (!MatchesShaderMapHash(shaderMap, shaderMapHash) || shaderMap.MaterialShaderMapContent == null) continue;
                if (!MatchesShaderPlatform(shaderMap, shaderPlatform)) continue;
                Dictionary<string, string> nameByHash = BuildPerMapNameDictionary(shaderMap);
                AppendShaderTruthRecords(result, shaderMap.MaterialShaderMapContent, nameByHash);
            }
        }
        if (result.Count > 0) return result;

        bool found = hashToMaterials.TryGetValue(shaderMapHash, out HashSet<string>? materials);
        if (DebugTrace) HookLogger.Log($"[TruthLookup] hash={shaderMapHash} platform={shaderPlatform} bridgeFound={found} materialCount={materials?.Count ?? 0}");
        if (found)
        {
            foreach (string materialPath in materials!)
            {
                bool inMI = output.MaterialInterfaces.TryGetValue(materialPath, out UnifiedMaterialMetadata? material);
                if (DebugTrace) HookLogger.Log($"[TruthLookup]   material={materialPath} inMI={inMI} shaderMaps={material?.LoadedShaderMaps?.Count ?? 0}");
                if (!inMI || material?.LoadedShaderMaps == null) continue;
                foreach (UnifiedShaderMapMetadata shaderMap in material.LoadedShaderMaps)
                {
                    bool platformOk = MatchesShaderPlatform(shaderMap, shaderPlatform);
                    if (DebugTrace) HookLogger.Log($"[TruthLookup]     sm.platform={shaderMap.ShaderPlatform} matches={platformOk} hasContent={shaderMap.MaterialShaderMapContent != null}");
                    if (shaderMap.MaterialShaderMapContent == null) continue;
                    if (!platformOk) continue;
                    Dictionary<string, string> nameByHash = BuildPerMapNameDictionary(shaderMap);
                    int before = result.Count;
                    AppendShaderTruthRecords(result, shaderMap.MaterialShaderMapContent, nameByHash);
                    if (DebugTrace) HookLogger.Log($"[TruthLookup]     appended={result.Count - before} truthRecords nameByHash={nameByHash.Count}");
                }
            }
        }

        return result;
    }

    private static bool MatchesShaderPlatform(UnifiedShaderMapMetadata shaderMap, string shaderPlatform)
    {
        return string.IsNullOrEmpty(shaderPlatform)
            || string.Equals(shaderMap.ShaderPlatform, shaderPlatform, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildPerMapNameDictionary(UnifiedShaderMapMetadata shaderMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UnifiedPointerTable? table = shaderMap.ShaderMapPointerTable;
        if (table == null) return result;

        if (table.TypeDependencies != null)
        {
            foreach (UnifiedTypeDependency dep in table.TypeDependencies)
            {
                if (string.IsNullOrWhiteSpace(dep.Name)) continue;
                string hash = HashedNamesResolver.HashName(dep.Name);
                result.TryAdd(hash, dep.Name);
                if (DebugTrace && (dep.Name.Contains("LumenCard") || dep.Name.Contains("BasePass") || dep.Name.Contains("Hit"))) HookLogger.Log($"[NameMap] {dep.Name} -> {hash}");
            }
        }
        if (table.Types != null)
        {
            foreach (UnifiedHashName entry in table.Types)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.Hash))
                {
                    result.TryAdd(entry.Hash, entry.Name!);
                }
            }
        }
        if (table.VertexFactoryTypes != null)
        {
            foreach (UnifiedHashName entry in table.VertexFactoryTypes)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.Hash))
                {
                    result.TryAdd(entry.Hash, entry.Name!);
                }
            }
        }
        return result;
    }

    private static bool MatchesShaderMapHash(UnifiedShaderMapMetadata shaderMap, string shaderMapHash)
    {
        return string.Equals(shaderMap.CookedShaderMapIdHash, shaderMapHash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(shaderMap.ShaderContentHash, shaderMapHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendShaderTruthRecords(List<StableShaderRecord> result, UnifiedShaderContent content, Dictionary<string, string> nameByHash)
    {
        int count = Math.Min(content.Shaders.Count, Math.Min(content.ShaderTypeHashes.Count, content.ShaderPermutations.Count));
        for (int i = 0; i < count; i++)
        {
            UnifiedShader shader = content.Shaders[i];
            string typeHash = PickNonEmpty(shader.TypeHash, content.ShaderTypeHashes[i]);
            string vfHash = NormalizeVertexFactoryHash(shader.VertexFactoryTypeHash);
            result.Add(new StableShaderRecord
            {
                ResourceIndex = shader.ResourceIndex,
                ShaderTypeHash = typeHash,
                ShaderTypeName = ResolveName(typeHash, nameByHash, HashedNamesResolver.ResolveShaderTypeName),
                VertexFactoryTypeHash = vfHash,
                VertexFactoryTypeName = ResolveName(vfHash, nameByHash, HashedNamesResolver.ResolveVertexFactoryTypeName),
                PermutationId = content.ShaderPermutations[i]
            });
        }

        foreach (UnifiedOrderedMeshShaderMap meshMap in content.OrderedMeshShaderMaps)
        {
            int meshCount = Math.Min(meshMap.Shaders.Count, Math.Min(meshMap.ShaderTypes.Count, meshMap.ShaderPermutations.Count));
            string meshVf = NormalizeVertexFactoryHash(meshMap.VertexFactoryType?.Hash);
            for (int i = 0; i < meshCount; i++)
            {
                UnifiedShader shader = meshMap.Shaders[i];
                string typeHash = PickNonEmpty(shader.TypeHash, meshMap.ShaderTypes[i].Hash);
                string vfHash = PickNonEmpty(NormalizeVertexFactoryHash(shader.VertexFactoryTypeHash), meshVf);
                result.Add(new StableShaderRecord
                {
                    ResourceIndex = shader.ResourceIndex,
                    ShaderTypeHash = typeHash,
                    ShaderTypeName = ResolveName(typeHash, nameByHash, HashedNamesResolver.ResolveShaderTypeName),
                    VertexFactoryTypeHash = vfHash,
                    VertexFactoryTypeName = ResolveName(vfHash, nameByHash, HashedNamesResolver.ResolveVertexFactoryTypeName),
                    PermutationId = meshMap.ShaderPermutations[i]
                });
            }
        }

        foreach (UnifiedShaderPipeline pipeline in content.ShaderPipelines)
        {
            int pipelineCount = Math.Min(pipeline.Shaders.Count, pipeline.PermutationIds.Count);
            for (int i = 0; i < pipelineCount; i++)
            {
                UnifiedShader shader = pipeline.Shaders[i];
                string vfHash = NormalizeVertexFactoryHash(shader.VertexFactoryTypeHash);
                result.Add(new StableShaderRecord
                {
                    ResourceIndex = shader.ResourceIndex,
                    ShaderTypeHash = shader.TypeHash,
                    ShaderTypeName = ResolveName(shader.TypeHash, nameByHash, HashedNamesResolver.ResolveShaderTypeName),
                    VertexFactoryTypeHash = vfHash,
                    VertexFactoryTypeName = ResolveName(vfHash, nameByHash, HashedNamesResolver.ResolveVertexFactoryTypeName),
                    PermutationId = pipeline.PermutationIds[i],
                    PipelineTypeHash = pipeline.TypeHash,
                    PipelineTypeName = ResolveName(pipeline.TypeHash, nameByHash, HashedNamesResolver.ResolvePipelineTypeName)
                });
            }
        }
    }

    private static string ResolveName(string hash, Dictionary<string, string> nameByHash, Func<string, string> fallback)
    {
        if (string.IsNullOrWhiteSpace(hash)) return string.Empty;
        if (nameByHash.TryGetValue(hash, out string? perMap) && !string.IsNullOrWhiteSpace(perMap))
        {
            return perMap;
        }
        return fallback(hash);
    }

    private static string PickNonEmpty(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred!;
        }

        return fallback ?? string.Empty;
    }

    private static string NormalizeVertexFactoryHash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "0000000000000000", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : value!;
    }

    private static string BuildContainerKey(string shaderMapHash, string shaderTypeHash, string vertexFactoryTypeHash)
    {
        string mapPart = ShortHash(shaderMapHash, 12);
        return $"SM{mapPart}";
    }

    private static string ShortHash(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "UNKNOWN";
        }

        string normalized = value!.Trim();
        return normalized.Length <= length ? normalized : normalized[..length];
    }

    private static void AddMaterialToHash(Dictionary<string, HashSet<string>> map, string hash, string material)
    {
        if (!map.TryGetValue(hash, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map[hash] = set;
        }
        set.Add(material);
    }
}
