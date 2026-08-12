using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Ruri.Hook.Core;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.RenderCore;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass030_ScanMaterialPackages
{
    public static void DoPass(ExportPipelineState state)
    {
        AbstractVfsFileProvider? provider = state.Provider;
        if (provider == null) return;

        BuildMaterialContexts(state, provider);
    }

    private static void BuildMaterialContexts(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;
        var cache = state.LoadedMaterialCache;

        if (output.PackageShaderMapHashes.Count == 0)
        {
            if (!state.MaterialScanComplete)
            {
                FullProviderScan(provider, output, cache, log);
                state.MaterialScanComplete = true;
                output.MaterialScanComplete = true;
            }
            return;
        }

        if (!state.MaterialScanComplete)
        {
            BuildResourceHashBridge(state, provider);
            state.MaterialScanComplete = true;
            output.MaterialScanComplete = true;
        }
        else
        {
            log($"    Material bridge: SKIPPED — {output.MaterialResourceHashes.Count} cached hash->material entries reused. Symbols not re-pulled.");
        }

        EnrichCurrentArchiveMaterials(state, provider);
    }

    private static void BuildResourceHashBridge(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;

        var containerKeys = new HashSet<string>(output.PackageShaderMapHashes.Keys, StringComparer.OrdinalIgnoreCase);
        var packageSet = new HashSet<string>(containerKeys, StringComparer.OrdinalIgnoreCase);
        foreach (GameFile file in provider.Files.Values)
        {
            if (IsPrefixedMaterialAsset(file)) packageSet.Add(file.PathWithoutExtension);
        }
        var packages = packageSet.ToList();
        log($"    Material bridge: START — reading inline shader-map hashes from {packages.Count} package(s) ({containerKeys.Count} container-header shader-map owners + {packages.Count - containerKeys.Count} material-prefixed).");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var bridge = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
        var emptyContainerPackages = new ConcurrentBag<string>();
        long withHashes = 0, failures = 0;
        int parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));

        System.Threading.Tasks.Parallel.ForEach(
            packages,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
            path =>
            {
                List<string>? hashes = LoadMaterialShaderMapHashes(provider, path);
                if (hashes == null || hashes.Count == 0)
                {
                    if (containerKeys.Contains(path)) emptyContainerPackages.Add(path);
                    return;
                }
                System.Threading.Interlocked.Increment(ref withHashes);
                foreach (string h in hashes)
                {
                    ConcurrentDictionary<string, byte> set = bridge.GetOrAdd(h, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                    set.TryAdd(path, 0);
                }
            });

        long recovered = 0;
        foreach (string path in emptyContainerPackages)
        {
            List<string>? hashes = LoadMaterialShaderMapHashes(provider, path);
            if (hashes == null) { System.Threading.Interlocked.Increment(ref failures); continue; }
            if (hashes.Count == 0) continue;
            System.Threading.Interlocked.Increment(ref recovered);
            foreach (string h in hashes)
            {
                ConcurrentDictionary<string, byte> set = bridge.GetOrAdd(h, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                set.TryAdd(path, 0);
            }
        }

        const int maxMaterialsPerHash = 16;
        foreach (KeyValuePair<string, ConcurrentDictionary<string, byte>> kvp in bridge)
        {
            output.MaterialResourceHashes[kvp.Key] = kvp.Value.Keys
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                .Take(maxMaterialsPerHash)
                .ToList();
        }

        log($"    Material bridge: DONE — packages={packages.Count}, with-shadermaps={withHashes + recovered} (parallel={withHashes} + single-thread-recovered={recovered}), bridge-hashes={output.MaterialResourceHashes.Count}, skipped-on-error={failures}, took {sw.Elapsed.TotalSeconds:F1}s.");
    }

    private static List<string>? LoadMaterialShaderMapHashes(AbstractVfsFileProvider provider, string packagePath)
    {
        CUE4Parse.UE4.Assets.IPackage? package;
        try
        {
            package = provider.LoadPackage(packagePath);
        }
        catch
        {
            return null;
        }
        if (package == null) return null;

        try
        {
            var result = new List<string>();
            foreach (CUE4Parse.UE4.Assets.Exports.UObject export in package.GetExports())
            {
                if (export is not UMaterialInterface material) continue;
                if (material.LoadedMaterialResources == null) break;
                foreach (var resource in material.LoadedMaterialResources)
                {
                    var shaderMap = resource.LoadedShaderMap;
                    if (shaderMap == null) continue;
                    string? resourceHash = shaderMap.ResourceHash?.ToString() ?? shaderMap.Code?.ResourceHash.ToString();
                    if (!string.IsNullOrWhiteSpace(resourceHash)) result.Add(resourceHash!);
                    string? cooked = shaderMap.ShaderMapId.CookedShaderMapIdHash?.ToString();
                    if (!string.IsNullOrWhiteSpace(cooked)) result.Add(cooked!);
                }
                break;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void EnrichCurrentArchiveMaterials(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;
        var cache = state.LoadedMaterialCache;
        HashSet<string> archiveHashes = state.CurrentArchiveShaderMapHashes;
        if (archiveHashes.Count == 0) return;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string hash in archiveHashes)
        {
            if (output.MaterialResourceHashes.TryGetValue(hash, out List<string>? materials) && materials.Count > 0)
            {
                paths.Add(materials[0]);
            }
        }

        long reused = 0, loaded = 0, failures = 0, produced = 0;
        foreach (string path in paths)
        {
            if (cache.ContainsKey(path)) { reused++; continue; }

            UnifiedMaterialMetadata? metadata = LoadAndExtractByPath(provider, path, out bool loadedOk, out bool failed);
            if (loadedOk) loaded++;
            if (failed) failures++;
            if (metadata != null && output.PackageShaderMapHashes.TryGetValue(path, out List<string>? hashes))
            {
                metadata.PackageShaderMapHashes = new List<string>(hashes);
            }
            cache[path] = metadata;
        }

        foreach (string path in paths)
        {
            if (cache.TryGetValue(path, out UnifiedMaterialMetadata? m) && m != null)
            {
                output.MaterialInterfaces[path] = m;
                produced++;
            }
        }

        log($"    Material enrich (archive-scoped): archive-hashes={archiveHashes.Count}, materials={paths.Count}, loaded={loaded}, reused={reused}, produced={produced}, skipped-on-error={failures}.");
    }

    private static void FullProviderScan(AbstractVfsFileProvider provider, UnifiedShaderMetadataRoot output, ConcurrentDictionary<string, UnifiedMaterialMetadata?> cache, Action<string> log)
    {
        var candidates = provider.Files.Values.Where(IsMaterialCandidate).ToList();
        log($"    Material scan (full): START — {candidates.Count} material candidate(s) to load.");

        long reused = 0;
        long loaded = 0;
        long loadFailures = 0;
        long extracted = 0;

        int parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));
        System.Threading.Tasks.Parallel.ForEach(
            candidates,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
            file =>
            {
                string packagePath = file.PathWithoutExtension;
                if (cache.ContainsKey(packagePath)) { System.Threading.Interlocked.Increment(ref reused); return; }

                UnifiedMaterialMetadata? metadata = LoadAndExtractByPath(provider, packagePath, out bool loadedOk, out bool failed);
                if (loadedOk) System.Threading.Interlocked.Increment(ref loaded);
                if (failed) System.Threading.Interlocked.Increment(ref loadFailures);
                if (metadata != null)
                {
                    if (output.PackageShaderMapHashes.TryGetValue(packagePath, out List<string>? hashes))
                        metadata.PackageShaderMapHashes = new List<string>(hashes);
                    System.Threading.Interlocked.Increment(ref extracted);
                }
                cache[packagePath] = metadata;
            });

        foreach (var file in candidates)
        {
            string packagePath = file.PathWithoutExtension;
            if (cache.TryGetValue(packagePath, out UnifiedMaterialMetadata? m) && m != null)
                output.MaterialInterfaces[packagePath] = m;
        }

        log($"    Material scan (full): candidates={candidates.Count}, cache-reused={reused}, loaded={loaded}, extracted={extracted}, skipped-on-error={loadFailures}.");
    }

    private static UnifiedMaterialMetadata? LoadAndExtractByPath(AbstractVfsFileProvider provider, string packagePath, out bool loadedOk, out bool failed)
    {
        loadedOk = false;
        failed = false;
        CUE4Parse.UE4.Assets.IPackage? package;
        try
        {
            package = provider.LoadPackage(packagePath);
            loadedOk = true;
        }
        catch (Exception ex)
        {
            failed = true;
            HookLogger.LogWarning($"[Pass030_ScanMaterialPackages] Skipped {packagePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (package == null) return null;

        try
        {
            UMaterialInterface? material = null;
            CUE4Parse.UE4.Assets.Exports.UObject? firstExport = null;
            foreach (CUE4Parse.UE4.Assets.Exports.UObject export in package.GetExports())
            {
                firstExport ??= export;
                if (export is UMaterialInterface mat)
                {
                    material = mat;
                    break;
                }
            }
            if (material != null)
            {
                return ExtractMaterialContext(material, packagePath);
            }
            if (firstExport != null)
            {
                return ExtractGenericContext(firstExport, packagePath);
            }
            return null;
        }
        catch (Exception ex)
        {
            failed = true;
            HookLogger.LogWarning($"[Pass030_ScanMaterialPackages] Extract failed for {packagePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static UnifiedMaterialMetadata? ExtractGenericContext(CUE4Parse.UE4.Assets.Exports.UObject asset, string packagePath)
    {
        var metadata = new UnifiedMaterialMetadata
        {
            MaterialPath = packagePath,
            CachedParameters = MaterialCachedExpressionReader.ReadGeneric(asset),
        };
        return metadata;
    }

    private static bool IsPrefixedMaterialAsset(GameFile file)
    {
        if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return false;
        string name = file.Name;
        return name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MPC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase);
    }

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
        if (name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MPC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (name.StartsWith("NS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NE_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSCS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NM_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return path.Contains("Material", StringComparison.OrdinalIgnoreCase);
    }

    private static UnifiedMaterialMetadata? ExtractMaterialContext(UMaterialInterface material, string materialPath)
    {
        var metadata = new UnifiedMaterialMetadata
        {
            MaterialPath = materialPath,
            RenderState = BuildRenderState(material)
        };

        if (material.LoadedMaterialResources != null && material.LoadedMaterialResources.Count > 0)
        {
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
                        : null,
                    ResourceHash = shaderMap.ResourceHash?.ToString() ?? shaderMap.Code?.ResourceHash.ToString(),
                };



                if (shaderMap.Content is FMaterialShaderMapContent materialContent)
                {
                    shaderMapMetadata.MaterialShaderMapContent = BuildShaderContent(materialContent, shaderMap.PointerTable as FShaderMapPointerTable);
                }

                metadata.LoadedShaderMaps.Add(shaderMapMetadata);
            }
        }

        metadata.CachedParameters = MaterialCachedExpressionReader.Read(material);

        return metadata;
    }

    private static UnifiedMaterialRenderState? BuildRenderState(UMaterialInterface material)
    {
        UnifiedMaterialRenderState rs = new();

        if (material is UMaterial umat)
        {
            rs.BlendMode = umat.BlendMode.ToString();
            rs.ShadingModel = umat.ShadingModel.ToString();
            rs.TranslucencyLightingMode = umat.TranslucencyLightingMode.ToString();
            rs.TwoSided = umat.TwoSided;
            rs.DisableDepthTest = umat.bDisableDepthTest;
            rs.IsMasked = umat.bIsMasked;
            rs.OpacityMaskClipValue = umat.OpacityMaskClipValue;
        }

        if (material is UMaterialInstance instance && instance.BasePropertyOverrides != null)
        {
            rs.HasInstanceOverrides = true;
            rs.BlendModeOverridden = true;
            rs.BlendMode = instance.BasePropertyOverrides.BlendMode.ToString();
            rs.ShadingModelOverridden = true;
            rs.ShadingModel = instance.BasePropertyOverrides.ShadingModel.ToString();
            rs.OpacityMaskClipValueOverridden = true;
            rs.OpacityMaskClipValue = instance.BasePropertyOverrides.OpacityMaskClipValue;
            rs.DitheredLODTransition = instance.BasePropertyOverrides.DitheredLODTransition;

            if (instance.Parent is UMaterial parentMat)
            {
                if (!rs.TwoSided) rs.TwoSided = parentMat.TwoSided;
                if (!rs.DisableDepthTest) rs.DisableDepthTest = parentMat.bDisableDepthTest;
                if (!rs.IsMasked) rs.IsMasked = parentMat.bIsMasked;
                rs.TranslucencyLightingMode = parentMat.TranslucencyLightingMode.ToString();
            }
        }

        if (material.TryGetValue(out FName domainName, "MaterialDomain") && !domainName.IsNone)
        {
            rs.MaterialDomain = domainName.Text;
        }
        if (material.TryGetValue(out FName blendableLoc, "BlendableLocation") && !blendableLoc.IsNone)
        {
            rs.BlendableLocation = blendableLoc.Text;
        }
        if (!rs.DitheredLODTransition && material.TryGetValue(out bool dithered, "DitheredLODTransition"))
        {
            rs.DitheredLODTransition = dithered;
        }

        return rs;
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


    private static UnifiedShaderContent BuildShaderContent(FMaterialShaderMapContent content, FShaderMapPointerTable? pointerTable)
    {
        return new UnifiedShaderContent
        {
            UniformExpressionSet = BuildUniformExpressionSet(content.MaterialCompilationOutput?.UniformExpressionSet),
            Shaders = content.Shaders?.Select(BuildShader).ToList() ?? new List<UnifiedShader>(),
            OrderedMeshShaderMaps = content.OrderedMeshShaderMaps?.Select(m => m == null ? new UnifiedOrderedMeshShaderMap() : BuildMeshShaderMap(m)).ToList() ?? new List<UnifiedOrderedMeshShaderMap>(),
        };
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

    private static UnifiedShader BuildShader(FShader shader)
    {
        return new UnifiedShader
        {
            ResourceIndex = shader.ResourceIndex,
            NumInstructions = shader.NumInstructions,
            SortKey = shader.SortKey,
            TypeHash = ResolveIndexedTypeHash(shader.Type),
            VertexFactoryTypeHash = ResolveIndexedTypeHash(shader.VFType),
            ParameterMapInfo = BuildShaderParameterMapInfo(shader.ParameterMapInfo),
        };
    }

    private static UnifiedOrderedMeshShaderMap BuildMeshShaderMap(FMeshMaterialShaderMap meshMap)
    {
        return new UnifiedOrderedMeshShaderMap
        {
            VertexFactoryType = new UnifiedHashName { Hash = ResolveIndexedTypeHash(meshMap.VertexFactoryTypeName) },
            ShaderTypes = meshMap.ShaderTypes?.Select(t => new UnifiedHashName { Hash = ResolveIndexedTypeHash(t) }).ToList() ?? new List<UnifiedHashName>(),
            ShaderPermutations = meshMap.ShaderPermutations?.ToList() ?? new List<int>(),
            Shaders = meshMap.Shaders?.Select(s => s == null ? new UnifiedShader() : BuildShader(s)).ToList() ?? new List<UnifiedShader>(),
        };
    }

    private static string ResolveIndexedTypeHash(FHashedName hashedName)
    {
        return hashedName.Hash != 0 ? hashedName.Hash.ToString("X16") : string.Empty;
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
                BaseIndex = checked((byte)parameter.BaseIndex),
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
                Type = parameter is FShaderResourceParameterInfo typed ? (byte)typed.Type : (byte)0
            }).ToList() ?? new List<UnifiedShaderResourceParameterInfo>(),
            SRVs = parameterMapInfo.SRVs?.Select(parameter => new UnifiedShaderResourceParameterInfo
            {
                BaseIndex = parameter.BaseIndex,
                Size = parameter.Size,
                BufferIndex = parameter is FShaderResourceParameterInfo resource ? resource.BufferIndex : (byte)0,
                Type = parameter is FShaderResourceParameterInfo typed ? (byte)typed.Type : (byte)0
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
