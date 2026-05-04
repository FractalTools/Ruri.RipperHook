using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ruri.Hook.Core;
using FModel.ViewModels;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.RenderCore;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Objects.Core.Math;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    internal static class UnifiedShaderMetadataExporter
    {
        // Material+package data is per-FModel-session, not per-library, so we
        // build it once and reuse across every per-library sidecar export.
        // Building MaterialInterfaces is the expensive step (loads every
        // material UAsset) — without this cache, ExportLibrarySidecarsOnly
        // would re-iterate the entire material catalog on every library hit.
        private static UnifiedShaderMetadataRoot? _sharedRoot;
        private static readonly object _sharedRootLock = new object();

        public static void ExportAll(CUE4ParseViewModel vm, GameFile? shaderLibraryEntry = null, string? shaderLibraryExportBasePath = null)
        {
            var provider = vm.Provider;
            if (provider == null)
            {
                return;
            }

            var output = BuildOrGetSharedRoot(vm);
            // ShaderCodeArchives is per-library; mutate the shared root by
            // adding the current library's archive metadata.
            BuildShaderLibraryMetadata(shaderLibraryEntry, output);

            if (output.MaterialInterfaces.Count == 0 && output.PackageShaderMapHashes.Count == 0 && output.ShaderCodeArchives.Count == 0)
            {
                HookLogger.LogWarning("[UnifiedShaderMetadataExporter] No verified shader metadata found to export.");
                return;
            }

            var projectName = provider.ProjectName ?? "UnknownProject";
            var outputPath = Path.Combine(FModel.Settings.UserSettings.Default.RawDataDirectory, projectName, "UnifiedShaderMetadata.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(output, Formatting.Indented));

            if (shaderLibraryEntry != null && !string.IsNullOrWhiteSpace(shaderLibraryExportBasePath))
            {
                ExportLibrarySidecars(output, shaderLibraryEntry, shaderLibraryExportBasePath);
            }

            HookLogger.LogSuccess($"[UnifiedShaderMetadataExporter] Exported unified metadata for {output.MaterialInterfaces.Count} materials, {output.PackageShaderMapHashes.Count} package→shader-map associations.");
        }

        public static void ExportLibrarySidecarsOnly(CUE4ParseViewModel vm, GameFile shaderLibraryEntry, string shaderLibraryExportBasePath)
        {
            var provider = vm.Provider;
            if (provider == null)
            {
                return;
            }

            // Use the same fully-built root the unified-metadata path uses.
            // Previously this path skipped BuildMaterialContexts, so sidecars
            // only saw IoStore-container shader-map hashes — any shader map
            // not registered in StoreEntries[i].ShaderMapHashes ended up with
            // 0 assets and was silently dropped from the sidecar. That is
            // the direct cause of mass UnknownShaderXXXXXX names downstream.
            var output = BuildOrGetSharedRoot(vm);
            BuildShaderLibraryMetadata(shaderLibraryEntry, output);

            if (output.ShaderCodeArchives.Count == 0)
            {
                HookLogger.LogWarning("[UnifiedShaderMetadataExporter] No shader library metadata found for sidecar export.");
                return;
            }

            ExportLibrarySidecars(output, shaderLibraryEntry, shaderLibraryExportBasePath);
            HookLogger.LogSuccess($"[UnifiedShaderMetadataExporter] Exported sidecars for {shaderLibraryEntry.PathWithoutExtension} (materials={output.MaterialInterfaces.Count}, package-hashes={output.PackageShaderMapHashes.Count}).");
        }

        private static UnifiedShaderMetadataRoot BuildOrGetSharedRoot(CUE4ParseViewModel vm)
        {
            lock (_sharedRootLock)
            {
                if (_sharedRoot != null)
                {
                    return _sharedRoot;
                }

                var root = new UnifiedShaderMetadataRoot();
                BuildShaderMapHashes(vm, root);
                if (vm.Provider != null)
                {
                    BuildMaterialContexts(vm.Provider, root);
                }
                _sharedRoot = root;
                return root;
            }
        }

        private static void ExportLibrarySidecars(UnifiedShaderMetadataRoot output, GameFile shaderLibraryEntry, string shaderLibraryExportBasePath)
        {
            if (!output.ShaderCodeArchives.TryGetValue(shaderLibraryEntry.PathWithoutExtension, out var library))
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

            File.WriteAllText(shaderLibraryExportBasePath + ".assetinfo.json", JsonConvert.SerializeObject(assetInfo, Formatting.Indented));
            File.WriteAllText(shaderLibraryExportBasePath + ".stableinfo.json", JsonConvert.SerializeObject(stableInfo, Formatting.Indented));
            HookLogger.Log($"[UnifiedShaderMetadataExporter] {Path.GetFileName(shaderLibraryExportBasePath)}: linked={linked} shader-maps, unlinked={unlinked}.");
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
                        HookLogger.Log($"[TruthLookup-FORCE] before-call result.Count={before} nameByHash.Count={nameByHash.Count} content.Shaders={shaderMap.MaterialShaderMapContent.Shaders?.Count ?? -1} meshMaps={shaderMap.MaterialShaderMapContent.OrderedMeshShaderMaps?.Count ?? -1}");
                        try
                        {
                            AppendShaderTruthRecords(result, shaderMap.MaterialShaderMapContent, nameByHash);
                        }
                        catch (Exception ex)
                        {
                            HookLogger.LogFailure($"[TruthLookup-FORCE] AppendShaderTruthRecords threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                            throw;
                        }
                        HookLogger.Log($"[TruthLookup-FORCE] after-call result.Count={result.Count} delta={result.Count - before}");
                    }
                }
            }

            return result;
        }

        private static readonly bool DebugTrace = string.Equals(Environment.GetEnvironmentVariable("RURI_TRUTH_DEBUG"), "1", StringComparison.Ordinal);

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
                    string hash = UeHashedNameResolver.HashName(dep.Name);
                    result.TryAdd(hash, dep.Name);
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
                    ShaderTypeName = ResolveName(typeHash, nameByHash, UeHashedNameResolver.ResolveShaderTypeName),
                    VertexFactoryTypeHash = vfHash,
                    VertexFactoryTypeName = ResolveName(vfHash, nameByHash, UeHashedNameResolver.ResolveVertexFactoryTypeName),
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
                        ShaderTypeName = ResolveName(typeHash, nameByHash, UeHashedNameResolver.ResolveShaderTypeName),
                        VertexFactoryTypeHash = vfHash,
                        VertexFactoryTypeName = ResolveName(vfHash, nameByHash, UeHashedNameResolver.ResolveVertexFactoryTypeName),
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
                        ShaderTypeName = ResolveName(shader.TypeHash, nameByHash, UeHashedNameResolver.ResolveShaderTypeName),
                        VertexFactoryTypeHash = vfHash,
                        VertexFactoryTypeName = ResolveName(vfHash, nameByHash, UeHashedNameResolver.ResolveVertexFactoryTypeName),
                        PermutationId = pipeline.PermutationIds[i],
                        PipelineTypeHash = pipeline.TypeHash,
                        PipelineTypeName = ResolveName(pipeline.TypeHash, nameByHash, UeHashedNameResolver.ResolvePipelineTypeName)
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
            string typePart = ShortHash(shaderTypeHash, 16);
            string vfPart = string.IsNullOrWhiteSpace(vertexFactoryTypeHash) ? "NOVF" : ShortHash(vertexFactoryTypeHash, 16);
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

        private static void BuildShaderLibraryMetadata(GameFile? shaderLibraryEntry, UnifiedShaderMetadataRoot output)
        {
            if (shaderLibraryEntry == null || !shaderLibraryEntry.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var archiveReader = shaderLibraryEntry.CreateReader();
            var archive = new FShaderCodeArchive(archiveReader);
            var libraryMetadata = new UnifiedShaderLibraryMetadata
            {
                LibraryPath = shaderLibraryEntry.PathWithoutExtension,
                LibraryName = shaderLibraryEntry.NameWithoutExtension,
                LibraryType = archive.SerializedShaders.GetType().Name
            };

            if (archive.SerializedShaders is FSerializedShaderArchive serialized)
            {
                libraryMetadata.ShaderMapHashes = serialized.ShaderMapHashes.Select(x => x.ToString()).ToList();
                libraryMetadata.ShaderHashes = serialized.ShaderHashes.Select(x => x.ToString()).ToList();
                libraryMetadata.ShaderMapEntries = serialized.ShaderMapEntries.Select(entry => new UnifiedShaderMapArchiveEntry
                {
                    ShaderIndicesOffset = entry.ShaderIndicesOffset,
                    NumShaders = entry.NumShaders,
                    FirstPreloadIndex = entry.FirstPreloadIndex,
                    NumPreloadEntries = entry.NumPreloadEntries
                }).ToList();
                libraryMetadata.ShaderEntries = serialized.ShaderEntries.Select(entry => new UnifiedShaderArchiveEntry
                {
                    Offset = entry.Offset,
                    Size = entry.Size,
                    UncompressedSize = entry.UncompressedSize,
                    Frequency = entry.Frequency
                }).ToList();
                libraryMetadata.ShaderIndices = serialized.ShaderIndices.ToList();
            }
            else if (archive.SerializedShaders is FIoStoreShaderCodeArchive ioStore)
            {
                libraryMetadata.ShaderMapHashes = ioStore.ShaderMapHashes.Select(x => x.ToString()).ToList();
                libraryMetadata.ShaderHashes = ioStore.ShaderHashes.Select(x => x.ToString()).ToList();
                libraryMetadata.ShaderMapEntries = ioStore.ShaderMapEntries.Select(entry => new UnifiedShaderMapArchiveEntry
                {
                    ShaderIndicesOffset = entry.ShaderIndicesOffset,
                    NumShaders = entry.NumShaders,
                    FirstPreloadIndex = 0,
                    NumPreloadEntries = 0
                }).ToList();
                libraryMetadata.ShaderEntries = ioStore.ShaderEntries.Select(entry => new UnifiedShaderArchiveEntry
                {
                    Offset = (ulong)entry.UncompressedOffsetInGroup,
                    Size = 0,
                    UncompressedSize = 0,
                    Frequency = (byte)entry.Frequency
                }).ToList();
                libraryMetadata.ShaderIndices = ioStore.ShaderIndices.ToList();
            }

            output.ShaderCodeArchives[shaderLibraryEntry.PathWithoutExtension] = libraryMetadata;
        }

        private static void BuildShaderMapHashes(CUE4ParseViewModel vm, UnifiedShaderMetadataRoot output)
        {
            var provider = vm.Provider;
            if (provider == null)
            {
                return;
            }

            var readers = provider.MountedVfs.Concat(provider.UnloadedVfs);
            foreach (var reader in readers)
            {
                if (reader is not IoStoreReader ioReader || ioReader.ContainerHeader == null)
                {
                    continue;
                }

                var header = ioReader.ContainerHeader;
                var packageIds = header.PackageIds;
                var storeEntries = header.StoreEntries;
                if (packageIds == null || storeEntries == null || packageIds.Length != storeEntries.Length)
                {
                    continue;
                }

                for (int i = 0; i < packageIds.Length; i++)
                {
                    var entry = storeEntries[i];
                    if (entry.ShaderMapHashes == null || entry.ShaderMapHashes.Length == 0)
                    {
                        continue;
                    }

                    if (!ioReader.PackageIdIndex.TryGetValue(packageIds[i], out var gameFile))
                    {
                        continue;
                    }

                    string packageName = gameFile.PathWithoutExtension;
                    output.PackageShaderMapHashes[packageName] = entry.ShaderMapHashes.Select(h => h.ToString()).ToList();
                }
            }
        }

        private static void BuildMaterialContexts(AbstractVfsFileProvider provider, UnifiedShaderMetadataRoot output)
        {
            int considered = 0;
            int loaded = 0;
            int loadFailures = 0;
            int extracted = 0;

            foreach (var file in provider.Files.Values)
            {
                if (!IsMaterialCandidate(file)) continue;
                considered++;

                CUE4Parse.UE4.Assets.Exports.UObject? asset;
                try
                {
                    asset = provider.LoadPackageObject(file.PathWithoutExtension);
                    loaded++;
                }
                catch (Exception ex)
                {
                    // One bad asset (Widget Blueprint, broken FName, missing
                    // schema, etc.) used to abort the whole loop and leave
                    // UnifiedShaderMetadata.json unwritten — which in turn
                    // collapses the decompiler's name resolution to a counter
                    // for every shader. Skip and keep going.
                    loadFailures++;
                    HookLogger.LogWarning($"[UnifiedShaderMetadataExporter] Skipped {file.Path}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (asset is not UMaterialInterface material)
                {
                    continue;
                }

                UnifiedMaterialMetadata? metadata;
                try
                {
                    metadata = ExtractMaterialContext(material, file.PathWithoutExtension);
                }
                catch (Exception ex)
                {
                    loadFailures++;
                    HookLogger.LogWarning($"[UnifiedShaderMetadataExporter] ExtractMaterialContext failed for {file.Path}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (metadata == null)
                {
                    continue;
                }

                output.MaterialInterfaces[file.PathWithoutExtension] = metadata;
                extracted++;
            }

            HookLogger.Log($"[UnifiedShaderMetadataExporter] Material scan: candidates={considered}, loaded={loaded}, extracted={extracted}, skipped-on-error={loadFailures}.");
        }

        // Tighter than the original `Path.Contains("/Material")`. Old check
        // matched things like `/Game/UI/Materials/WBP_ShadowSample` (a
        // Widget Blueprint) which exploded inside LoadPackageObject. Here we
        // exclude obvious non-material prefixes, then accept a much wider
        // heuristic: any `Material` substring (case-insensitive) anywhere
        // in the path. Engine materials live under
        // `/Engine/Content/EngineMaterials/`, `/Engine/Content/EditorMaterials/`,
        // `/Engine/Content/EngineDebugMaterials/`, etc. — those need to load
        // because the asset-info sidecar links cooked shader-maps back to
        // them, and stripping them stripped the type-name back-fill source
        // for all engine-material shader-maps.
        // directory or to use one of UE's standard material naming prefixes,
        // and we explicitly exclude Blueprint / Widget Blueprint / DataAsset
        // prefixes that can sit in the same folder.
        private static bool IsMaterialCandidate(GameFile file)
        {
            if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string name = file.Name;
            if (name.StartsWith("WBP_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ABP_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("DA_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string path = file.Path;
            // Material naming conventions (asset-side): hard accept.
            if (name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MPC_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Path-side accept: any path containing "Material" (case-insensitive).
            // Catches "/Materials/", "/EngineMaterials/", "/EditorMaterials/",
            // "/EngineDebugMaterials/", "/MasterMaterials/", "/EngineSky/" (no, that
            // doesn't have Material — fine, won't accept). The caller still gates
            // on `asset is UMaterialInterface` so the LoadPackageObject failures
            // are non-fatal and just contribute to skipped-on-error.
            return path.Contains("Material", StringComparison.OrdinalIgnoreCase);
        }

        private static UnifiedMaterialMetadata? ExtractMaterialContext(UMaterialInterface material, string materialPath)
        {
            if (material.LoadedMaterialResources == null || material.LoadedMaterialResources.Count == 0)
            {
                return null;
            }

            var metadata = new UnifiedMaterialMetadata
            {
                MaterialPath = materialPath
            };

            foreach (var resource in material.LoadedMaterialResources)
            {
                if (resource.LoadedShaderMap == null)
                {
                    continue;
                }

                var shaderMap = resource.LoadedShaderMap;
                var shaderMapMetadata = new UnifiedShaderMapMetadata
                {
                    ShaderPlatform = shaderMap.ShaderPlatform.ToString(),
                    CookedShaderMapIdHash = shaderMap.ShaderMapId.CookedShaderMapIdHash?.ToString(),
                    ShaderContentHash = shaderMap.Content is FMaterialShaderMapContent materialShaderMapContent
                        ? materialShaderMapContent.ShaderContentHash.ToString()
                        : null
                };

                if (shaderMap.PointerTable is FShaderMapPointerTable pointerTable)
                {
                    shaderMapMetadata.ShaderMapPointerTable = BuildPointerTable(pointerTable);
                }

                if (shaderMap.FrozenArchive != null)
                {
                    shaderMapMetadata.MemoryImageResult = BuildFrozenArchive(shaderMap.FrozenArchive);
                }

                if (shaderMap.Content is FMaterialShaderMapContent materialContent)
                {
                    shaderMapMetadata.MaterialShaderMapContent = BuildShaderContent(materialContent, shaderMap.PointerTable as FShaderMapPointerTable);
                }

                metadata.LoadedShaderMaps.Add(shaderMapMetadata);
            }

            return metadata.LoadedShaderMaps.Count > 0 ? metadata : null;
        }

        private static UnifiedPointerTable BuildPointerTable(FShaderMapPointerTable pointerTable)
        {
            var result = new UnifiedPointerTable();

            if (pointerTable.Types != null)
            {
                result.Types = pointerTable.Types.Select(type => new UnifiedHashName
                {
                    Hash = type.Hash.ToString("X16")
                }).ToList();
            }

            if (pointerTable.VFTypes != null)
            {
                result.VertexFactoryTypes = pointerTable.VFTypes.Select(type => new UnifiedHashName
                {
                    Hash = type.Hash.ToString("X16")
                }).ToList();
            }

            if (pointerTable.TypeDependencies != null)
            {
                result.TypeDependencies = pointerTable.TypeDependencies.Select(type => new UnifiedTypeDependency
                {
                    Name = type.Name?.ToString() ?? string.Empty,
                    SavedLayoutSize = type.SavedLayoutSize,
                    SavedLayoutHash = type.SavedLayoutHash.ToString()
                }).ToList();
            }

            return result;
        }

        private static UnifiedFrozenArchive BuildFrozenArchive(FMemoryImageResult frozenArchive)
        {
            var result = new UnifiedFrozenArchive();

            result.FrozenObjectBase64 = Convert.ToBase64String(frozenArchive.FrozenObject ?? Array.Empty<byte>());

            if (frozenArchive.ScriptNames != null)
            {
                result.ScriptNames = frozenArchive.ScriptNames.Select(name => new UnifiedFrozenName
                {
                    Name = name.Name.Text,
                    Patches = name.Patches?.Select(patch => patch.Offset).ToList() ?? new List<int>()
                }).ToList();
            }

            if (frozenArchive.MinimalNames != null)
            {
                result.MinimalNames = frozenArchive.MinimalNames.Select(name => new UnifiedFrozenName
                {
                    Name = name.Name.Text,
                    Patches = name.Patches?.Select(patch => patch.Offset).ToList() ?? new List<int>()
                }).ToList();
            }

            if (frozenArchive.VTables != null)
            {
                result.VTables = frozenArchive.VTables.Select(vtable => new UnifiedFrozenVTable
                {
                    TypeNameHash = vtable.TypeNameHash.ToString("X16"),
                    Patches = vtable.Patches?.Select(patch => new UnifiedFrozenVTablePatch
                    {
                        Offset = patch.Offset,
                        VTableOffset = patch.VTableOffset
                    }).ToList() ?? new List<UnifiedFrozenVTablePatch>()
                }).ToList();
            }

            return result;
        }

        private static UnifiedShaderContent BuildShaderContent(FMaterialShaderMapContent content, FShaderMapPointerTable? pointerTable)
        {
            var result = new UnifiedShaderContent();

            result.UniformExpressionSet = BuildUniformExpressionSet(content.MaterialCompilationOutput?.UniformExpressionSet);

            if (content.ShaderTypes != null)
            {
                result.ShaderTypeHashes = content.ShaderTypes.Select(type => type.Hash.ToString("X16")).ToList();
            }

            if (content.ShaderPermutations != null)
            {
                result.ShaderPermutations = content.ShaderPermutations.ToList();
            }

            if (content.Shaders != null)
            {
                result.Shaders = content.Shaders.Select(shader => BuildShader(shader, pointerTable)).ToList();
            }

            if (content.ShaderPipelines != null)
            {
                result.ShaderPipelines = content.ShaderPipelines.Select(pipeline => BuildShaderPipeline(pipeline, pointerTable)).ToList();
            }

            if (content.OrderedMeshShaderMaps != null)
            {
                result.OrderedMeshShaderMaps = content.OrderedMeshShaderMaps.Select(meshMap =>
                {
                    var mesh = new UnifiedOrderedMeshShaderMap
                    {
                        VertexFactoryType = new UnifiedHashName
                        {
                            Hash = meshMap.VertexFactoryTypeName.Hash.ToString("X16")
                        }
                    };

                    if (meshMap.ShaderTypes != null)
                    {
                        mesh.ShaderTypes = meshMap.ShaderTypes.Select(type => new UnifiedHashName
                        {
                            Hash = type.Hash.ToString("X16")
                        }).ToList();
                    }

                    if (meshMap.ShaderPermutations != null)
                    {
                        mesh.ShaderPermutations = meshMap.ShaderPermutations.ToList();
                    }

                    if (meshMap.Shaders != null)
                    {
                        mesh.Shaders = meshMap.Shaders.Where(shader => shader != null).Select(shader => BuildShader(shader, pointerTable)).ToList();
                    }

                    return mesh;
                }).ToList();
            }

            return result;
        }

        private static UnifiedUniformExpressionSet? BuildUniformExpressionSet(FUniformExpressionSet? uniformExpressionSet)
        {
            if (uniformExpressionSet == null)
            {
                return null;
            }

            return new UnifiedUniformExpressionSet
            {
                UniformPreshaders = uniformExpressionSet.UniformPreshaders?.Select(BuildPreshaderHeader).ToList() ?? new List<UnifiedMaterialUniformPreshaderHeader>(),
                UniformPreshaderFields = uniformExpressionSet.UniformPreshaderFields?.Select(field => new UnifiedMaterialUniformPreshaderField
                {
                    BufferOffset = field.BufferOffset,
                    ComponentIndex = field.ComponentIndex,
                    Type = field.Type.ToString()
                }).ToList() ?? new List<UnifiedMaterialUniformPreshaderField>(),
                UniformNumericParameters = uniformExpressionSet.UniformNumericParameters?.Select(parameter => new UnifiedMaterialNumericParameter
                {
                    ParameterName = parameter.ParameterInfo.Name.Text,
                    Association = parameter.ParameterInfo.Association.ToString(),
                    Index = parameter.ParameterInfo.Index,
                    ParameterType = parameter.ParameterType.ToString(),
                    DefaultValueOffset = parameter.DefaultValueOffset,
                    Value = ConvertMaterialParameterValue(parameter.Value)
                }).ToList() ?? new List<UnifiedMaterialNumericParameter>(),
                UniformTextureParameters = uniformExpressionSet.UniformTextureParameters?.Select(textureParameters =>
                    textureParameters?.Select(BuildTextureParameterInfo).ToList() ?? new List<UnifiedMaterialTextureParameter>()).ToList()
                    ?? new List<List<UnifiedMaterialTextureParameter>>(),
                UniformExternalTextureParameters = uniformExpressionSet.UniformExternalTextureParameters?.Select(parameter => new UnifiedMaterialExternalTextureParameter
                {
                    ParameterName = parameter.ParameterName.Text,
                    ExternalTextureGuid = parameter.ExternalTextureGuid.ToString(),
                    SourceTextureIndex = parameter.SourceTextureIndex
                }).ToList() ?? new List<UnifiedMaterialExternalTextureParameter>(),
                UniformTextureCollectionParameters = uniformExpressionSet.UniformTextureCollectionParameters?.Select(parameter => new UnifiedMaterialTextureCollectionParameter
                {
                    TextureCollectionIndex = parameter.TextureCollectionIndex,
                    ParameterName = parameter.ParameterInfo.Name.ToString(),
                    Association = parameter.ParameterInfo.Association.ToString(),
                    Index = parameter.ParameterInfo.Index,
                    IsVirtualCollection = parameter.bisVirtualCollection
                }).ToList() ?? new List<UnifiedMaterialTextureCollectionParameter>(),
                ParameterCollections = uniformExpressionSet.ParameterCollections?.Select(guid => guid.ToString()).ToList() ?? new List<string>(),
                UniformPreshaderBufferSize = uniformExpressionSet.UniformPreshaderBufferSize,
                UniformBufferLayoutInitializer = BuildUniformBufferLayoutInitializer(uniformExpressionSet.UniformBufferLayoutInitializer),
                UniformPreshaderData = BuildPreshaderData(uniformExpressionSet.UniformPreshaderData)
            };
        }

        private static UnifiedMaterialTextureParameter BuildTextureParameterInfo(FMaterialTextureParameterInfo parameter)
        {
            return new UnifiedMaterialTextureParameter
            {
                ParameterName = GetMaterialParameterName(parameter),
                Association = GetMaterialParameterAssociation(parameter),
                Index = GetMaterialParameterIndex(parameter),
                TextureIndex = parameter.TextureIndex,
                SamplerSource = parameter.SamplerSource.ToString(),
                VirtualTextureLayerIndex = parameter.VirtualTextureLayerIndex
            };
        }

        private static UnifiedUniformBufferLayoutInitializer BuildUniformBufferLayoutInitializer(FRHIUniformBufferLayoutInitializer layout)
        {
            return new UnifiedUniformBufferLayoutInitializer
            {
                Name = layout.Name,
                Resources = BuildUniformBufferResources(layout.Resources),
                GraphResources = BuildUniformBufferResources(layout.GraphResources),
                GraphTextures = BuildUniformBufferResources(layout.GraphTextures),
                GraphBuffers = BuildUniformBufferResources(layout.GraphBuffers),
                GraphUniformBuffers = BuildUniformBufferResources(layout.GraphUniformBuffers),
                UniformBuffers = BuildUniformBufferResources(layout.UniformBuffers),
                Hash = layout.Hash,
                ConstantBufferSize = layout.ConstantBufferSize,
                RenderTargetsOffset = layout.RenderTargetsOffset,
                StaticSlot = layout.StaticSlot,
                BindingFlags = layout.BindingFlags.ToString(),
                HasNonGraphOutputs = layout.Flags.HasFlag(ERHIUniformBufferFlags.HasNonGraphOutputs),
                NoEmulatedUniformBuffer = layout.Flags.HasFlag(ERHIUniformBufferFlags.NoEmulatedUniformBuffer),
                UniformView = layout.Flags.HasFlag(ERHIUniformBufferFlags.UniformView)
            };
        }

        private static List<UnifiedUniformBufferResource> BuildUniformBufferResources(FRHIUniformBufferResource[]? resources)
        {
            return resources?.Select(resource => new UnifiedUniformBufferResource
            {
                MemberOffset = resource.MemberOffset,
                MemberType = resource.MemberType.ToString()
            }).ToList() ?? new List<UnifiedUniformBufferResource>();
        }

        private static string GetMaterialParameterName(FMaterialBaseParameterInfo parameter)
        {
            if (parameter.ParameterInfo != null)
            {
                return parameter.ParameterInfo.Name.Text;
            }

            if (parameter.ParameterInfoOld != null)
            {
                return parameter.ParameterInfoOld.Name.ToString();
            }

            return parameter.ParameterName ?? string.Empty;
        }

        private static string GetMaterialParameterAssociation(FMaterialBaseParameterInfo parameter)
        {
            if (parameter.ParameterInfo != null)
            {
                return parameter.ParameterInfo.Association.ToString();
            }

            if (parameter.ParameterInfoOld != null)
            {
                return parameter.ParameterInfoOld.Association.ToString();
            }

            return string.Empty;
        }

        private static int GetMaterialParameterIndex(FMaterialBaseParameterInfo parameter)
        {
            if (parameter.ParameterInfo != null)
            {
                return parameter.ParameterInfo.Index;
            }

            if (parameter.ParameterInfoOld != null)
            {
                return parameter.ParameterInfoOld.Index;
            }

            return 0;
        }

        private static UnifiedMaterialUniformPreshaderHeader BuildPreshaderHeader(FMaterialUniformPreshaderHeader header)
        {
            var result = new UnifiedMaterialUniformPreshaderHeader
            {
                OpcodeOffset = header.OpcodeOffset,
                OpcodeSize = header.OpcodeSize
            };

            if (header is FMaterialUniformPreshaderHeader_5_1 header51)
            {
                result.FieldIndex = header51.FieldIndex;
                result.NumFields = header51.NumFields;
            }

            if (header is FMaterialUniformPreshaderHeader_5_0 header50)
            {
                result.BufferOffset = header50.BufferOffset;
                result.ComponentType = header50.ComponentType.ToString();
                result.NumComponents = header50.NumComponents;
            }

            if (header is FMaterialUniformPreshaderHeader_5_8 header58)
            {
                result.BufferOffset = header58.BufferOffset;
                result.Type = header58.Type.ToString();
            }

            return result;
        }

        private static UnifiedMaterialPreshaderData BuildPreshaderData(FMaterialPreshaderData preshaderData)
        {
            return new UnifiedMaterialPreshaderData
            {
                Names = preshaderData.Names?.Select(name => name.Text).ToList() ?? new List<string>(),
                NamesOffset = preshaderData.NamesOffset?.ToList() ?? new List<uint>(),
                StructTypes = preshaderData.StructTypes?.Select(type => new UnifiedPreshaderStructType
                {
                    Hash = type.Hash.ToString("X16"),
                    ComponentTypeIndex = type.ComponentTypeIndex,
                    NumComponents = type.NumComponents
                }).ToList() ?? new List<UnifiedPreshaderStructType>(),
                StructComponentTypes = preshaderData.StructComponentTypes?.Select(type => type.ToString()).ToList() ?? new List<string>(),
                Data = Convert.ToBase64String(preshaderData.Data ?? Array.Empty<byte>()),
                IsPreshader2 = preshaderData.bPreshader2
            };
        }

        private static object? ConvertMaterialParameterValue(object? value)
        {
            return value switch
            {
                null => null,
                FLinearColor color => new UnifiedLinearColor
                {
                    R = color.R,
                    G = color.G,
                    B = color.B,
                    A = color.A
                },
                FVector4 vector => new UnifiedVector4
                {
                    X = (double)vector.X,
                    Y = (double)vector.Y,
                    Z = (double)vector.Z,
                    W = (double)vector.W
                },
                _ => value
            };
        }

        private static UnifiedShader BuildShader(FShader shader, FShaderMapPointerTable? pointerTable)
        {
            return new UnifiedShader
            {
                ResourceIndex = shader.ResourceIndex,
                NumInstructions = shader.NumInstructions,
                SortKey = shader.SortKey,
                TypeHash = ResolveIndexedTypeHash(shader.Type, pointerTable?.Types),
                VertexFactoryTypeHash = ResolveIndexedTypeHash(shader.VFType, pointerTable?.VFTypes),
                UniformBufferParameterStructHashes = shader.UniformBufferParameterStructs?.Select(x => x.Hash.ToString("X16")).ToList() ?? new List<string>(),
                UniformBufferParameterStructs = shader.UniformBufferParameterStructs?.Select(x => new UnifiedHashName
                {
                    Hash = x.Hash.ToString("X16")
                }).ToList() ?? new List<UnifiedHashName>(),
                UniformBufferParameterBaseIndices = shader.UniformBufferParameters?.Select(x => x.BaseIndex).ToList() ?? new List<ushort>(),
                Bindings = BuildShaderBindings(shader.Bindings),
                ParameterMapInfo = BuildShaderParameterMapInfo(shader.ParameterMapInfo)
            };
        }

        private static UnifiedShaderPipeline BuildShaderPipeline(FShaderPipeline pipeline, FShaderMapPointerTable? pointerTable)
        {
            return new UnifiedShaderPipeline
            {
                TypeHash = pipeline.TypeName.Hash.ToString("X16"),
                Shaders = pipeline.Shaders?.Where(shader => shader != null).Select(shader => BuildShader(shader, pointerTable)).ToList() ?? new List<UnifiedShader>(),
                PermutationIds = pipeline.PermutationIds?.ToList() ?? new List<int>()
            };
        }

        private static string ResolveIndexedTypeHash(ulong packedIndexedPtr, FHashedName[]? table)
        {
            if ((packedIndexedPtr & 1UL) != 0 && table != null)
            {
                ulong index = packedIndexedPtr >> 1;
                if (index < (ulong)table.Length)
                {
                    return table[(int)index].Hash.ToString("X16");
                }
            }

            return packedIndexedPtr != 0 ? packedIndexedPtr.ToString("X16") : string.Empty;
        }

        private static UnifiedShaderBindings BuildShaderBindings(FShaderParameterBindings bindings)
        {
            return new UnifiedShaderBindings
            {
                Parameters = bindings.Parameters?.Select(parameter => new UnifiedBindingParameter
                {
                    BufferIndex = parameter.BufferIndex,
                    BaseIndex = parameter.BaseIndex,
                    ByteOffset = parameter.ByteOffset,
                    ByteSize = parameter.ByteSize
                }).ToList() ?? new List<UnifiedBindingParameter>(),
                ResourceParameters = bindings.ResourceParameters?.Select(parameter => new UnifiedResourceBindingParameter
                {
                    ByteOffset = parameter.ByteOffset,
                    BaseIndex = parameter.BaseIndex,
                    BaseType = parameter.BaseType.ToString()
                }).ToList() ?? new List<UnifiedResourceBindingParameter>(),
                BindlessResourceParameters = bindings.BindlessResourceParameters?.Select(parameter => new UnifiedBindlessResourceParameter
                {
                    ByteOffset = parameter.ByteOffset,
                    GlobalConstantOffset = parameter.GlobalConstantOffset,
                    BaseType = parameter.BaseType.ToString()
                }).ToList() ?? new List<UnifiedBindlessResourceParameter>(),
                GraphUniformBuffers = bindings.GraphUniformBuffers?.Select(parameter => new UnifiedParameterStructReference
                {
                    BufferIndex = parameter.BufferIndex,
                    ByteOffset = parameter.ByteOffset
                }).ToList() ?? new List<UnifiedParameterStructReference>(),
                ParameterReferences = bindings.ParameterReferences?.Select(parameter => new UnifiedParameterStructReference
                {
                    BufferIndex = parameter.BufferIndex,
                    ByteOffset = parameter.ByteOffset
                }).ToList() ?? new List<UnifiedParameterStructReference>(),
                StructureLayoutHash = bindings.StructureLayoutHash,
                RootParameterBufferIndex = bindings.RootParameterBufferIndex
            };
        }

        private static UnifiedShaderParameterMapInfo BuildShaderParameterMapInfo(FShaderParameterMapInfo parameterMapInfo)
        {
            return new UnifiedShaderParameterMapInfo
            {
                UniformBuffers = parameterMapInfo.UniformBuffers?.Select(parameter => new UnifiedShaderParameterInfo
                {
                    BaseIndex = parameter.BaseIndex,
                    Size = parameter.Size
                }).ToList() ?? new List<UnifiedShaderParameterInfo>(),
                TextureSamplers = parameterMapInfo.TextureSamplers?.Select(parameter => new UnifiedShaderResourceParameterInfo
                {
                    BaseIndex = parameter.BaseIndex,
                    Size = parameter.Size,
                    BufferIndex = parameter is FShaderResourceParameterInfo resource ? resource.BufferIndex : (byte)0,
                    Type = parameter is FShaderResourceParameterInfo typed ? typed.Type : (byte)0
                }).ToList() ?? new List<UnifiedShaderResourceParameterInfo>(),
                SRVs = parameterMapInfo.SRVs?.Select(parameter => new UnifiedShaderResourceParameterInfo
                {
                    BaseIndex = parameter.BaseIndex,
                    Size = parameter.Size,
                    BufferIndex = parameter is FShaderResourceParameterInfo resource ? resource.BufferIndex : (byte)0,
                    Type = parameter is FShaderResourceParameterInfo typed ? typed.Type : (byte)0
                }).ToList() ?? new List<UnifiedShaderResourceParameterInfo>(),
                LooseParameterBuffers = parameterMapInfo.LooseParameterBuffers?.Select(buffer => new UnifiedShaderLooseParameterBufferInfo
                {
                    BaseIndex = buffer.BaseIndex,
                    Size = buffer.Size,
                    Parameters = buffer.Parameters?.Select(parameter => new UnifiedShaderParameterInfo
                    {
                        BaseIndex = parameter.BaseIndex,
                        Size = parameter.Size
                    }).ToList() ?? new List<UnifiedShaderParameterInfo>()
                }).ToList() ?? new List<UnifiedShaderLooseParameterBufferInfo>(),
                Hash = parameterMapInfo.Hash.ToString("X16")
            };
        }
    }

    internal sealed class UnifiedShaderMetadataRoot
    {
        public Dictionary<string, List<string>> PackageShaderMapHashes { get; set; } = new();
        public Dictionary<string, UnifiedMaterialMetadata> MaterialInterfaces { get; set; } = new();
        public Dictionary<string, UnifiedShaderLibraryMetadata> ShaderCodeArchives { get; set; } = new();
    }

    internal sealed class UnifiedShaderLibraryMetadata
    {
        public string LibraryPath { get; set; } = string.Empty;
        public string LibraryName { get; set; } = string.Empty;
        public string LibraryType { get; set; } = string.Empty;
        public List<string> ShaderMapHashes { get; set; } = new();
        public List<string> ShaderHashes { get; set; } = new();
        public List<UnifiedShaderMapArchiveEntry> ShaderMapEntries { get; set; } = new();
        public List<UnifiedShaderArchiveEntry> ShaderEntries { get; set; } = new();
        public List<uint> ShaderIndices { get; set; } = new();
    }

    internal sealed class ShaderAssetInfoEquivalent
    {
        public int AssetInfoVersion { get; set; } = 2;
        public List<ShaderAssetInfoEntry> ShaderCodeToAssets { get; set; } = new();
    }

    internal sealed class ShaderAssetInfoEntry
    {
        public string ShaderMapHash { get; set; } = string.Empty;
        public List<string> Assets { get; set; } = new();
    }

    internal sealed class ShaderStableInfoEquivalent
    {
        public string LibraryPath { get; set; } = string.Empty;
        public string LibraryName { get; set; } = string.Empty;
        public string LibraryType { get; set; } = string.Empty;
        public List<ShaderStableInfoEntry> ShaderMaps { get; set; } = new();
    }

    internal sealed class ShaderStableInfoEntry
    {
        public string ShaderMapHash { get; set; } = string.Empty;
        public List<string> Assets { get; set; } = new();
        public List<string> ShaderHashes { get; set; } = new();
        public List<byte> Frequencies { get; set; } = new();
        public List<StableShaderRecord> Shaders { get; set; } = new();
        public List<string> Types { get; set; } = new();
        public List<string> VertexFactoryTypes { get; set; } = new();
        public List<string> ShaderTypeHashes { get; set; } = new();
        public List<string> UniformBufferParameterStructHashes { get; set; } = new();
    }

    internal sealed class StableShaderRecord
    {
        public int ArchiveShaderIndex { get; set; } = -1;
        public int ResourceIndex { get; set; } = -1;
        public string ShaderHash { get; set; } = string.Empty;
        public byte Frequency { get; set; }
        public string ShaderTypeHash { get; set; } = string.Empty;
        public string ShaderTypeName { get; set; } = string.Empty;
        public string VertexFactoryTypeHash { get; set; } = string.Empty;
        public string VertexFactoryTypeName { get; set; } = string.Empty;
        public int PermutationId { get; set; } = -1;
        public string PipelineTypeHash { get; set; } = string.Empty;
        public string PipelineTypeName { get; set; } = string.Empty;
        public string ContainerKey { get; set; } = string.Empty;
    }

    internal sealed class UnifiedShaderMapArchiveEntry
    {
        public uint ShaderIndicesOffset { get; set; }
        public uint NumShaders { get; set; }
        public uint FirstPreloadIndex { get; set; }
        public uint NumPreloadEntries { get; set; }
    }

    internal sealed class UnifiedShaderArchiveEntry
    {
        public ulong Offset { get; set; }
        public uint Size { get; set; }
        public uint UncompressedSize { get; set; }
        public byte Frequency { get; set; }
    }

    internal sealed class UnifiedMaterialMetadata
    {
        public string MaterialPath { get; set; } = string.Empty;
        public List<UnifiedShaderMapMetadata> LoadedShaderMaps { get; set; } = new();
    }

    internal sealed class UnifiedShaderMapMetadata
    {
        public string? ShaderPlatform { get; set; }
        public string? CookedShaderMapIdHash { get; set; }
        public string? ShaderContentHash { get; set; }
        public UnifiedPointerTable? ShaderMapPointerTable { get; set; }
        public UnifiedFrozenArchive? MemoryImageResult { get; set; }
        public UnifiedShaderContent? MaterialShaderMapContent { get; set; }
    }

    internal sealed class UnifiedPointerTable
    {
        public List<UnifiedHashName> Types { get; set; } = new();
        public List<UnifiedHashName> VertexFactoryTypes { get; set; } = new();
        public List<UnifiedTypeDependency> TypeDependencies { get; set; } = new();
    }

    internal sealed class UnifiedHashName
    {
        public string Hash { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    internal sealed class UnifiedTypeDependency
    {
        public string Name { get; set; } = string.Empty;
        public uint SavedLayoutSize { get; set; }
        public string SavedLayoutHash { get; set; } = string.Empty;
    }

    internal sealed class UnifiedFrozenArchive
    {
        public string FrozenObjectBase64 { get; set; } = string.Empty;
        public List<UnifiedFrozenName> ScriptNames { get; set; } = new();
        public List<UnifiedFrozenName> MinimalNames { get; set; } = new();
        public List<UnifiedFrozenVTable> VTables { get; set; } = new();
    }

    internal sealed class UnifiedFrozenName
    {
        public string Name { get; set; } = string.Empty;
        public List<int> Patches { get; set; } = new();
    }

    internal sealed class UnifiedFrozenVTable
    {
        public string TypeNameHash { get; set; } = string.Empty;
        public List<UnifiedFrozenVTablePatch> Patches { get; set; } = new();
    }

    internal sealed class UnifiedFrozenVTablePatch
    {
        public int Offset { get; set; }
        public int VTableOffset { get; set; }
    }

    internal sealed class UnifiedShaderContent
    {
        public UnifiedUniformExpressionSet? UniformExpressionSet { get; set; }
        public List<string> ShaderTypeHashes { get; set; } = new();
        public List<int> ShaderPermutations { get; set; } = new();
        public List<UnifiedShader> Shaders { get; set; } = new();
        public List<UnifiedShaderPipeline> ShaderPipelines { get; set; } = new();
        public List<UnifiedOrderedMeshShaderMap> OrderedMeshShaderMaps { get; set; } = new();
    }

    internal sealed class UnifiedShaderPipeline
    {
        public string TypeHash { get; set; } = string.Empty;
        public List<UnifiedShader> Shaders { get; set; } = new();
        public List<int> PermutationIds { get; set; } = new();
    }

    internal sealed class UnifiedUniformExpressionSet
    {
        public List<UnifiedMaterialUniformPreshaderHeader> UniformPreshaders { get; set; } = new();
        public List<UnifiedMaterialUniformPreshaderField> UniformPreshaderFields { get; set; } = new();
        public List<UnifiedMaterialNumericParameter> UniformNumericParameters { get; set; } = new();
        public List<List<UnifiedMaterialTextureParameter>> UniformTextureParameters { get; set; } = new();
        public List<UnifiedMaterialExternalTextureParameter> UniformExternalTextureParameters { get; set; } = new();
        public List<UnifiedMaterialTextureCollectionParameter> UniformTextureCollectionParameters { get; set; } = new();
        public List<string> ParameterCollections { get; set; } = new();
        public uint UniformPreshaderBufferSize { get; set; }
        public UnifiedUniformBufferLayoutInitializer? UniformBufferLayoutInitializer { get; set; }
        public UnifiedMaterialPreshaderData? UniformPreshaderData { get; set; }
    }

    internal sealed class UnifiedMaterialUniformPreshaderHeader
    {
        public uint OpcodeOffset { get; set; }
        public uint OpcodeSize { get; set; }
        public uint? FieldIndex { get; set; }
        public uint? NumFields { get; set; }
        public uint? BufferOffset { get; set; }
        public string? ComponentType { get; set; }
        public byte? NumComponents { get; set; }
        public string? Type { get; set; }
    }

    internal sealed class UnifiedMaterialUniformPreshaderField
    {
        public uint BufferOffset { get; set; }
        public uint ComponentIndex { get; set; }
        public string Type { get; set; } = string.Empty;
    }

    internal sealed class UnifiedMaterialNumericParameter
    {
        public string ParameterName { get; set; } = string.Empty;
        public string Association { get; set; } = string.Empty;
        public int Index { get; set; }
        public string ParameterType { get; set; } = string.Empty;
        public uint DefaultValueOffset { get; set; }
        public object? Value { get; set; }
    }

    internal sealed class UnifiedMaterialTextureParameter
    {
        public string ParameterName { get; set; } = string.Empty;
        public string Association { get; set; } = string.Empty;
        public int Index { get; set; }
        public int TextureIndex { get; set; }
        public string SamplerSource { get; set; } = string.Empty;
        public byte VirtualTextureLayerIndex { get; set; }
    }

    internal sealed class UnifiedMaterialExternalTextureParameter
    {
        public string ParameterName { get; set; } = string.Empty;
        public string ExternalTextureGuid { get; set; } = string.Empty;
        public int SourceTextureIndex { get; set; }
    }

    internal sealed class UnifiedMaterialTextureCollectionParameter
    {
        public int TextureCollectionIndex { get; set; }
        public string ParameterName { get; set; } = string.Empty;
        public string Association { get; set; } = string.Empty;
        public int Index { get; set; }
        public bool IsVirtualCollection { get; set; }
    }

    internal sealed class UnifiedMaterialPreshaderData
    {
        public List<string> Names { get; set; } = new();
        public List<uint> NamesOffset { get; set; } = new();
        public List<UnifiedPreshaderStructType> StructTypes { get; set; } = new();
        public List<string> StructComponentTypes { get; set; } = new();
        public string Data { get; set; } = string.Empty;
        public bool IsPreshader2 { get; set; }
    }

    internal sealed class UnifiedUniformBufferLayoutInitializer
    {
        public string Name { get; set; } = string.Empty;
        public List<UnifiedUniformBufferResource> Resources { get; set; } = new();
        public List<UnifiedUniformBufferResource> GraphResources { get; set; } = new();
        public List<UnifiedUniformBufferResource> GraphTextures { get; set; } = new();
        public List<UnifiedUniformBufferResource> GraphBuffers { get; set; } = new();
        public List<UnifiedUniformBufferResource> GraphUniformBuffers { get; set; } = new();
        public List<UnifiedUniformBufferResource> UniformBuffers { get; set; } = new();
        public uint Hash { get; set; }
        public uint ConstantBufferSize { get; set; }
        public ushort RenderTargetsOffset { get; set; }
        public byte StaticSlot { get; set; }
        public string BindingFlags { get; set; } = string.Empty;
        public bool HasNonGraphOutputs { get; set; }
        public bool NoEmulatedUniformBuffer { get; set; }
        public bool UniformView { get; set; }
    }

    internal sealed class UnifiedUniformBufferResource
    {
        public ushort MemberOffset { get; set; }
        public string MemberType { get; set; } = string.Empty;
    }

    internal sealed class UnifiedPreshaderStructType
    {
        public string Hash { get; set; } = string.Empty;
        public int ComponentTypeIndex { get; set; }
        public int NumComponents { get; set; }
    }

    internal sealed class UnifiedLinearColor
    {
        public float R { get; set; }
        public float G { get; set; }
        public float B { get; set; }
        public float A { get; set; }
    }

    internal sealed class UnifiedVector4
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double W { get; set; }
    }

    internal sealed class UnifiedOrderedMeshShaderMap
    {
        public UnifiedHashName? VertexFactoryType { get; set; }
        public List<UnifiedHashName> ShaderTypes { get; set; } = new();
        public List<int> ShaderPermutations { get; set; } = new();
        public List<UnifiedShader> Shaders { get; set; } = new();
    }

    internal sealed class UnifiedShader
    {
        public int ResourceIndex { get; set; }
        public uint NumInstructions { get; set; }
        public uint SortKey { get; set; }
        public string TypeHash { get; set; } = string.Empty;
        public string VertexFactoryTypeHash { get; set; } = string.Empty;
        public List<string> UniformBufferParameterStructHashes { get; set; } = new();
        public List<UnifiedHashName> UniformBufferParameterStructs { get; set; } = new();
        public List<ushort> UniformBufferParameterBaseIndices { get; set; } = new();
        public UnifiedShaderBindings? Bindings { get; set; }
        public UnifiedShaderParameterMapInfo? ParameterMapInfo { get; set; }
    }

    internal sealed class UnifiedShaderBindings
    {
        public List<UnifiedBindingParameter> Parameters { get; set; } = new();
        public List<UnifiedResourceBindingParameter> ResourceParameters { get; set; } = new();
        public List<UnifiedBindlessResourceParameter> BindlessResourceParameters { get; set; } = new();
        public List<UnifiedParameterStructReference> GraphUniformBuffers { get; set; } = new();
        public List<UnifiedParameterStructReference> ParameterReferences { get; set; } = new();
        public uint StructureLayoutHash { get; set; }
        public ushort RootParameterBufferIndex { get; set; }
    }

    internal sealed class UnifiedBindingParameter
    {
        public ushort BufferIndex { get; set; }
        public ushort BaseIndex { get; set; }
        public ushort ByteOffset { get; set; }
        public ushort ByteSize { get; set; }
    }

    internal sealed class UnifiedResourceBindingParameter
    {
        public ushort ByteOffset { get; set; }
        public byte BaseIndex { get; set; }
        public string BaseType { get; set; } = string.Empty;
    }

    internal sealed class UnifiedBindlessResourceParameter
    {
        public ushort ByteOffset { get; set; }
        public ushort GlobalConstantOffset { get; set; }
        public string BaseType { get; set; } = string.Empty;
    }

    internal sealed class UnifiedParameterStructReference
    {
        public ushort BufferIndex { get; set; }
        public ushort ByteOffset { get; set; }
    }

    internal sealed class UnifiedShaderParameterMapInfo
    {
        public List<UnifiedShaderParameterInfo> UniformBuffers { get; set; } = new();
        public List<UnifiedShaderResourceParameterInfo> TextureSamplers { get; set; } = new();
        public List<UnifiedShaderResourceParameterInfo> SRVs { get; set; } = new();
        public List<UnifiedShaderLooseParameterBufferInfo> LooseParameterBuffers { get; set; } = new();
        public string Hash { get; set; } = string.Empty;
    }

    internal class UnifiedShaderParameterInfo
    {
        public ushort BaseIndex { get; set; }
        public ushort Size { get; set; }
    }

    internal sealed class UnifiedShaderResourceParameterInfo : UnifiedShaderParameterInfo
    {
        public byte BufferIndex { get; set; }
        public byte Type { get; set; }
    }

    internal sealed class UnifiedShaderLooseParameterBufferInfo
    {
        public ushort BaseIndex { get; set; }
        public ushort Size { get; set; }
        public List<UnifiedShaderParameterInfo> Parameters { get; set; } = new();
    }

    internal sealed class StableShaderTruthAggregate
    {
        public HashSet<string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VertexFactoryTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ShaderTypeHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UniformBufferParameterStructHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(UnifiedShader shader)
        {
            if (!string.IsNullOrWhiteSpace(shader.TypeHash))
            {
                Types.Add(shader.TypeHash);
                ShaderTypeHashes.Add(shader.TypeHash);
            }

            if (!string.IsNullOrWhiteSpace(shader.VertexFactoryTypeHash) && !string.Equals(shader.VertexFactoryTypeHash, "0000000000000000", StringComparison.OrdinalIgnoreCase))
            {
                VertexFactoryTypes.Add(shader.VertexFactoryTypeHash);
            }

            foreach (string hash in shader.UniformBufferParameterStructHashes)
            {
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    UniformBufferParameterStructHashes.Add(hash);
                }
            }
        }
    }
}
