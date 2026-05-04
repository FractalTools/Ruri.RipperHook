using AssetRipper.Assets.Generics;
using AssetRipper.Assets;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgramParameters;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderFloatValue;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderRTBlendState;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderState;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderVectorValue;
using AssetRipper.SourceGenerated.Subclasses.SerializedStencilOp;
using AssetRipper.SourceGenerated.Subclasses.SerializedTagMap;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.AR;

internal static class UnityShaderMetadataBuilder
{
    public readonly record struct ProgramBlobReference(uint BlobIndex, uint? ParameterBlobIndex, List<ushort> KeywordIndices);

    public readonly record struct ProgramResultLocation(int SubShaderIndex, int PassIndex, string Stage, uint BlobIndex, uint? ParameterBlobIndex, List<ushort> KeywordIndices);

    public static UnityShaderMetadata Build(
        IShader shader,
        GPUPlatform platform,
        Func<ISerializedProgram, UnityVersion, GPUPlatform, IEnumerable<ProgramBlobReference>> enumerateProgramBlobIndices)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(enumerateProgramBlobIndices);

        var parsedForm = shader.ParsedForm!;
        UnityShaderMetadata metadata = new()
        {
            m_ObjectHideFlags = 0,
            m_Name = shader.Name,
            m_CustomEditorName = string.Empty,
            m_FallbackName = parsedForm.FallbackName?.String ?? string.Empty,
            platforms = shader.Platforms?.Select(static p => (int)p).ToList() ?? [],
            compressedBlob = shader.CompressedBlob ?? [],
        };

        metadata.m_ParsedForm.m_Name = parsedForm.Name_R;
        metadata.m_ParsedForm.m_FallbackName = parsedForm.FallbackName?.String ?? string.Empty;
        metadata.offsets = ReadUInt32Matrix(shader.Offsets_AssetList_AssetList_UInt32, shader.Offsets_AssetList_UInt32);
        metadata.compressedLengths = ReadUInt32Matrix(shader.CompressedLengths_AssetList_AssetList_UInt32, shader.CompressedLengths_AssetList_UInt32);
        metadata.decompressedLengths = ReadUInt32Matrix(shader.DecompressedLengths_AssetList_AssetList_UInt32, shader.DecompressedLengths_AssetList_UInt32);

        if (parsedForm.Has_CustomEditorForRenderPipelines() && parsedForm.CustomEditorForRenderPipelines is not null)
        {
            metadata.m_CustomEditorForRenderPipelines = parsedForm.CustomEditorForRenderPipelines.Select(static item => new UnitySerializedCustomEditorForRenderPipeline
            {
                customEditorName = item.CustomEditorName.String,
                renderPipelineType = item.RenderPipelineType.String,
            }).ToList();
        }

        if (parsedForm.PropInfo is not null)
        {
            foreach (var property in parsedForm.PropInfo.Props)
            {
                metadata.m_ParsedForm.m_PropInfo.m_Props.Add(new UnitySerializedProperty
                {
                    m_Name = property.Name_R,
                    m_Description = property.Description,
                    m_Attributes = property.Attributes?.Select(static s => s.String).ToList() ?? [],
                    m_Type = (int)property.Type,
                    m_Flags = (uint)property.Flags,
                    m_DefValue = [property.DefValue_0_, property.DefValue_1_, property.DefValue_2_, property.DefValue_3_],
                    m_DefTexture = new UnitySerializedTextureProperty
                    {
                        m_DefaultName = property.DefTexture.DefaultName.String,
                        m_TexDim = (int)property.DefTexture.TexDim,
                    },
                });
            }
        }

        metadata.m_ParsedForm.m_KeywordNames = parsedForm.KeywordNames?.Select(static s => s.String).ToList() ?? [];

        for (int subShaderIndex = 0; subShaderIndex < parsedForm.SubShaders.Count; subShaderIndex++)
        {
            var sourceSubShader = parsedForm.SubShaders[subShaderIndex];
            UnitySerializedSubShader subShader = new()
            {
                m_LOD = sourceSubShader.LOD,
            };
            CopyTags(sourceSubShader.Tags, subShader.m_Tags.tags);

            for (int passIndex = 0; passIndex < sourceSubShader.Passes.Count; passIndex++)
            {
                var sourcePass = sourceSubShader.Passes[passIndex];
                UnitySerializedPass pass = BuildPass(sourcePass, shader.Collection.Version, platform, enumerateProgramBlobIndices);
                subShader.m_Passes.Add(pass);
            }

            metadata.m_ParsedForm.m_SubShaders.Add(subShader);
        }

        return metadata;
    }

    public static void BackfillProgramSources(UnityShaderMetadata metadata, IReadOnlyList<ProgramResultLocation> locations, DecompileResult[] results)
    {
        for (int i = 0; i < locations.Count; i++)
        {
            ProgramResultLocation location = locations[i];
            DecompileResult result = results[i];
            UnityProgramData? program = metadata.m_ParsedForm.m_SubShaders[location.SubShaderIndex]
                .m_Passes[location.PassIndex]
                .Programs
                .FirstOrDefault(p => p.Stage == location.Stage && p.BlobIndex == location.BlobIndex && p.ParameterBlobIndex == location.ParameterBlobIndex && p.KeywordIndices.SequenceEqual(location.KeywordIndices));
            if (program is null)
            {
                continue;
            }

            program.Success = result.Success;
            program.SourceCode = result.SourceCode;
            program.SourceLanguage = result.SourceLanguage;
            program.SourceFileExtension = result.SourceFileExtension;
            program.ErrorMessage = result.ErrorMessage;
        }
    }

    private static UnitySerializedPass BuildPass(
        ISerializedPass sourcePass,
        UnityVersion version,
        GPUPlatform platform,
        Func<ISerializedProgram, UnityVersion, GPUPlatform, IEnumerable<ProgramBlobReference>> enumerateProgramBlobIndices)
    {
        List<UnitySerializedNameIndex> nameIndices = [];
        for (int i = 0; i < sourcePass.NameIndices.Count; i++)
        {
            var pair = sourcePass.NameIndices.GetPair(i);
            nameIndices.Add(new UnitySerializedNameIndex
            {
                first = pair.Key.ToString(),
                second = pair.Value,
            });
        }

        UnitySerializedPass pass = new()
        {
            m_Type = (int)sourcePass.Type,
            m_UseName = sourcePass.UseName,
            m_Name = sourcePass.Name,
            m_TextureName = sourcePass.TextureName,
            m_ProgramMask = sourcePass.ProgramMask,
            m_HasInstancingVariant = sourcePass.HasInstancingVariant,
            m_HasProceduralInstancingVariant = sourcePass.Has_HasProceduralInstancingVariant() && sourcePass.HasProceduralInstancingVariant,
            m_NameIndices = nameIndices,
            m_State = new UnitySerializedShaderState
            {
                m_Name = sourcePass.State.Name_R,
                gpuProgramID = sourcePass.State.GpuProgramID,
                m_LOD = sourcePass.State.LOD,
            },
        };

        if (!string.IsNullOrWhiteSpace(pass.m_UseName))
        {
            return pass;
        }

        BuildPassState(sourcePass.State, pass);
        AddProgramPlaceholders(sourcePass.ProgVertex, version, platform, pass, "Vertex", enumerateProgramBlobIndices);
        AddProgramPlaceholders(sourcePass.ProgFragment, version, platform, pass, "Fragment", enumerateProgramBlobIndices);
        AddProgramPlaceholders(sourcePass.ProgGeometry, version, platform, pass, "Geometry", enumerateProgramBlobIndices);
        AddProgramPlaceholders(sourcePass.ProgHull, version, platform, pass, "Hull", enumerateProgramBlobIndices);
        AddProgramPlaceholders(sourcePass.ProgDomain, version, platform, pass, "Domain", enumerateProgramBlobIndices);
        AddProgramPlaceholders(sourcePass.ProgRayTracing, version, platform, pass, "RayTracing", enumerateProgramBlobIndices);
        return pass;
    }

    private static void AddProgramPlaceholders(
        ISerializedProgram? program,
        UnityVersion version,
        GPUPlatform platform,
        UnitySerializedPass pass,
        string stage,
        Func<ISerializedProgram, UnityVersion, GPUPlatform, IEnumerable<ProgramBlobReference>> enumerateProgramBlobIndices)
    {
        if (program is null)
        {
            return;
        }

        foreach (ProgramBlobReference blob in enumerateProgramBlobIndices(program, version, platform))
        {
            pass.Programs.Add(new UnityProgramData
            {
                Stage = stage,
                BlobIndex = blob.BlobIndex,
                ParameterBlobIndex = blob.ParameterBlobIndex,
                KeywordIndices = blob.KeywordIndices,
            });
        }
    }

    private static List<List<uint>> ReadUInt32Matrix(AssetList<AssetList<uint>>? nested, AssetList<uint>? flat)
    {
        if (nested is not null)
        {
            return nested.Select(static row => row.ToList()).ToList();
        }
        if (flat is not null)
        {
            return flat.Select(static value => new List<uint> { value }).ToList();
        }
        return [];
    }

    private static void BuildPassState(ISerializedShaderState state, UnitySerializedPass pass)
    {
        pass.m_State.rtSeparateBlend = state.RtSeparateBlend;
        pass.m_State.rtBlend0 = BuildRtBlendState(state.RtBlend0);
        pass.m_State.rtBlend1 = BuildRtBlendState(state.RtBlend1);
        pass.m_State.rtBlend2 = BuildRtBlendState(state.RtBlend2);
        pass.m_State.rtBlend3 = BuildRtBlendState(state.RtBlend3);
        pass.m_State.rtBlend4 = BuildRtBlendState(state.RtBlend4);
        pass.m_State.rtBlend5 = BuildRtBlendState(state.RtBlend5);
        pass.m_State.rtBlend6 = BuildRtBlendState(state.RtBlend6);
        pass.m_State.rtBlend7 = BuildRtBlendState(state.RtBlend7);
        pass.m_State.stencilOp = BuildStencilOp(state.StencilOp);
        pass.m_State.stencilOpFront = BuildStencilOp(state.StencilOpFront);
        pass.m_State.stencilOpBack = BuildStencilOp(state.StencilOpBack);
        pass.m_State.stencilRef = BuildFloatValue(state.StencilRef.Value, GetFloatValueName(state.StencilRef));
        pass.m_State.stencilReadMask = BuildFloatValue(state.StencilReadMask.Value, GetFloatValueName(state.StencilReadMask));
        pass.m_State.stencilWriteMask = BuildFloatValue(state.StencilWriteMask.Value, GetFloatValueName(state.StencilWriteMask));
        pass.m_State.fogMode = state.FogMode;
        pass.m_State.fogColor = BuildVectorValue(state.FogColor);
        pass.m_State.fogDensity = BuildFloatValue(state.FogDensity.Value, GetFloatValueName(state.FogDensity));
        pass.m_State.fogStart = BuildFloatValue(state.FogStart.Value, GetFloatValueName(state.FogStart));
        pass.m_State.fogEnd = BuildFloatValue(state.FogEnd.Value, GetFloatValueName(state.FogEnd));
        pass.m_State.alphaToMask = BuildFloatValue(state.AlphaToMask.Value, GetFloatValueName(state.AlphaToMask));
        pass.m_State.zClip = state.Has_ZClip() ? BuildFloatValue(state.ZClip!.Value, GetFloatValueName(state.ZClip)) : new UnitySerializedShaderFloatValue();
        pass.m_State.zTest = BuildFloatValue(state.ZTest.Value, GetFloatValueName(state.ZTest));
        pass.m_State.zWrite = BuildFloatValue(state.ZWrite.Value, GetFloatValueName(state.ZWrite));
        pass.m_State.culling = BuildFloatValue(state.Culling.Value, GetFloatValueName(state.Culling));
        pass.m_State.offsetFactor = BuildFloatValue(state.OffsetFactor.Value, GetFloatValueName(state.OffsetFactor));
        pass.m_State.offsetUnits = BuildFloatValue(state.OffsetUnits.Value, GetFloatValueName(state.OffsetUnits));
        pass.m_State.lighting = state.Lighting;
        CopyTags(state.Tags, pass.m_State.m_Tags.tags);
    }

    private static UnitySerializedShaderRTBlendState BuildRtBlendState(ISerializedShaderRTBlendState state)
    {
        return new UnitySerializedShaderRTBlendState
        {
            srcBlend = BuildFloatValue(state.SourceBlend.Value, GetFloatValueName(state.SourceBlend)),
            destBlend = BuildFloatValue(state.DestinationBlend.Value, GetFloatValueName(state.DestinationBlend)),
            srcBlendAlpha = BuildFloatValue(state.SourceBlendAlpha.Value, GetFloatValueName(state.SourceBlendAlpha)),
            destBlendAlpha = BuildFloatValue(state.DestinationBlendAlpha.Value, GetFloatValueName(state.DestinationBlendAlpha)),
            blendOp = BuildFloatValue(state.BlendOp.Value, GetFloatValueName(state.BlendOp)),
            blendOpAlpha = BuildFloatValue(state.BlendOpAlpha.Value, GetFloatValueName(state.BlendOpAlpha)),
            colMask = BuildFloatValue(state.ColMask.Value, GetFloatValueName(state.ColMask)),
        };
    }

    private static UnitySerializedStencilOp BuildStencilOp(ISerializedStencilOp state)
    {
        return new UnitySerializedStencilOp
        {
            pass = BuildFloatValue(state.Pass.Value, GetFloatValueName(state.Pass)),
            fail = BuildFloatValue(state.Fail.Value, GetFloatValueName(state.Fail)),
            zFail = BuildFloatValue(state.ZFail.Value, GetFloatValueName(state.ZFail)),
            comp = BuildFloatValue(state.Comp.Value, GetFloatValueName(state.Comp)),
        };
    }

    private static UnitySerializedShaderVectorValue BuildVectorValue(ISerializedShaderVectorValue value)
    {
        return new UnitySerializedShaderVectorValue
        {
            x = BuildFloatValue(value.X.Value, GetFloatValueName(value.X)),
            y = BuildFloatValue(value.Y.Value, GetFloatValueName(value.Y)),
            z = BuildFloatValue(value.Z.Value, GetFloatValueName(value.Z)),
            w = BuildFloatValue(value.W.Value, GetFloatValueName(value.W)),
        };
    }

    private static UnitySerializedShaderFloatValue BuildFloatValue(float value, string name)
    {
        return new UnitySerializedShaderFloatValue
        {
            val = value,
            name = name,
        };
    }

    private static string GetFloatValueName(ISerializedShaderFloatValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        string name = (value as INamed)?.Name?.ToString() ?? string.Empty;
        return string.Equals(name, "<noninit>", StringComparison.Ordinal) ? string.Empty : name;
    }

    private static void CopyTags(ISerializedTagMap? tags, List<UnityTagMapEntry> destination)
    {
        if (tags is null)
        {
            return;
        }

        foreach (var tag in tags.Tags)
        {
            destination.Add(new UnityTagMapEntry
            {
                first = tag.Key.String,
                second = tag.Value.String,
            });
        }
    }
}
