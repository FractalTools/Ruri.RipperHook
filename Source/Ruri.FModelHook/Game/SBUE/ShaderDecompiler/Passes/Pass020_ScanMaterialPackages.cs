using System;
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

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 020 — Walk every material UAsset known to FModel's provider and
// fold its `LoadedMaterialResources[*].LoadedShaderMap` graph into
// `state.Root.MaterialInterfaces`.
//
// This is the expensive step: each candidate UAsset is loaded via
// `provider.LoadPackageObject`, parsed, and unrolled into the unified
// metadata DTO graph (FShader -> FShaderMapPointerTable -> FFrozenArchive
// -> FMaterialShaderMapContent). The result is cached on the shared
// `ExportPipelineState` so subsequent library exports in the same FModel
// session reuse the work.
//
// Why this lives in a single pass file: every helper here is consumed by
// exactly one method (`ExtractMaterialContext`); they are inlined per the
// "no helpers outside passes" rule. Splitting them into per-DTO files
// would create a dense one-way using-cycle without any reuse benefit.
internal static class Pass020_ScanMaterialPackages
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.MaterialsScanned) return;
        AbstractVfsFileProvider? provider = state.Vm?.Provider;
        if (provider == null) return;

        BuildMaterialContexts(provider, state.Root, state.Log);
        state.MaterialsScanned = true;
    }

    private static void BuildMaterialContexts(AbstractVfsFileProvider provider, UnifiedShaderMetadataRoot output, Action<string> log)
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
                HookLogger.LogWarning($"[Pass020_ScanMaterialPackages] Skipped {file.Path}: {ex.GetType().Name}: {ex.Message}");
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
                HookLogger.LogWarning($"[Pass020_ScanMaterialPackages] ExtractMaterialContext failed for {file.Path}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (metadata == null)
            {
                continue;
            }

            output.MaterialInterfaces[file.PathWithoutExtension] = metadata;
            extracted++;
        }

        log($"    Material scan: candidates={considered}, loaded={loaded}, extracted={extracted}, skipped-on-error={loadFailures}.");
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
        // The caller still gates on `asset is UMaterialInterface` so the
        // LoadPackageObject failures are non-fatal and just contribute to
        // skipped-on-error.
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
