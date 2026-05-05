using System;
using System.Collections.Generic;
using System.Linq;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 050 — Compose the per-library `.assetinfo`/`.stableinfo` payloads
// (in-memory DTOs only; the actual file writes are Pass 060/070). For
// each shader-map in the current library:
//
//   1. Resolve owning materials via the combined hash -> materials map
//      (PackageShaderMapHashes + per-material LoadedShaderMaps).
//      Hash-misses produce no asset-info entry (asset-info is "things we
//      can name"); the stableinfo side skips them too.
//
//   2. Walk the shader-map's `ShaderIndicesOffset` slice to build the
//      hash + frequency lists, plus per-shader stable records that join
//      type/VF/permutation truth from the unified metadata graph using
//      `HashedNamesResolver` to recover symbolic names from hashes.
//
// All of the helpers below are consumed by exactly this pass; they were
// moved out of the old monolithic build pass and inlined per the
// "no helpers outside passes" rule.
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

        // Build a single hash → materials map that combines BOTH data
        // sources so material→shader-map associations missing from the
        // IoStore container header still surface here:
        //   1. PackageShaderMapHashes — IoStore StoreEntries[i].ShaderMapHashes
        //   2. MaterialInterfaces[*].LoadedShaderMaps[*].CookedShaderMapIdHash
        // Without source 2, any cooked shader map whose owning material's
        // package wasn't enumerated by the IoStore reader would have 0
        // assets and get dropped, leaving the decompiler with no name to
        // use → mass UnknownShader names.
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

        foreach (var kvp in output.MaterialInterfaces)
        {
            if (kvp.Value?.LoadedShaderMaps == null) continue;
            foreach (var sm in kvp.Value.LoadedShaderMaps)
            {
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

    // Extract UE shader-platform string (SP_<plat>) from a library's
    // archive name like `ShaderArchive-PineForestPack_Demo-PCD3D_SM5-PCD3D_SM5`.
    // This is the join key against `UnifiedShaderMapMetadata.ShaderPlatform`
    // ("SP_PCD3D_SM5") used to pick the right LoadedShaderMaps entry when
    // a material has multiple platforms cooked in.
    private static string ExtractShaderPlatform(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName)) return string.Empty;
        // ShaderArchive-{Project|Global}-{plat}-{plat}
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

            // The unified metadata's `ResourceIndex` is the shader-map-RELATIVE
            // slot inside this map (0..NumShaders-1) -- the same `i` we use to
            // walk ShaderIndices[]. The previous lookup used the GLOBAL
            // `shaderIndex`, which never matched, so every record fell through
            // to the empty defaults and stripped all type/permutation symbols.
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

    // Resolve the right material(s) for an on-disk shader-map hash:
    //   1. Try the direct hash → material match (CookedShaderMapIdHash /
    //      ShaderContentHash equals the on-disk hash). Editor / non-IoStore
    //      cooks tend to use this.
    //   2. If that misses, fall through to the asset-info bridge: the
    //      `hashToMaterials` map (built from sidecar + unified package
    //      hashes) tells us which material(s) own this on-disk hash, and
    //      we then pick each material's LoadedShaderMaps entry whose
    //      ShaderPlatform matches the archive being processed. IoStore
    //      cooks NEED this path because the package-level shader-map
    //      hash does NOT equal CookedShaderMapIdHash.
    private static List<StableShaderRecord> BuildOrderedTruthRecords(
        UnifiedShaderMetadataRoot output,
        string shaderMapHash,
        Dictionary<string, HashSet<string>> hashToMaterials,
        string shaderPlatform)
    {
        var result = new List<StableShaderRecord>();

        // Direct match first.
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

        // Asset-info bridge for IoStore cooks.
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
        // Empty platform string disables filtering (used for global archives
        // and unit tests); otherwise require an exact match against the
        // material's per-shader-map ShaderPlatform field.
        return string.IsNullOrEmpty(shaderPlatform)
            || string.Equals(shaderMap.ShaderPlatform, shaderPlatform, StringComparison.OrdinalIgnoreCase);
    }

    // Per-map FHashedName -> name dictionary built from the cooked
    // metadata's TypeDependencies and pointer table. UE strips Names
    // from the on-disk pointer table (UnifiedHashName.Name is null
    // for cooked builds) but records the full type-name list in
    // TypeDependencies. Hashing each TypeDependencies entry with
    // FHashedName recovers the hash -> name mapping for everything
    // referenced by THIS shader-map -- no UE source scan needed.
    //
    // This is the "non-hardcoded" path: names come from the game's
    // own cooked data, not from regex-matching IMPLEMENT_SHADER_TYPE
    // macros in a specific UE version's source tree.
    private static Dictionary<string, string> BuildPerMapNameDictionary(UnifiedShaderMapMetadata shaderMap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UnifiedPointerTable? table = shaderMap.ShaderMapPointerTable;
        if (table == null) return result;

        // Hash every TypeDependencies entry's name and index by hash.
        // The metadata may also include explicit hash-with-name pairs
        // (older cooks, editor builds) so harvest those too.
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

    // Per-map TypeDependencies dictionary takes precedence — it's
    // truthful for THIS specific cook, while the UE-source fallback
    // can lie for forks or version skews. Empty hash returns empty.
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
