using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
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
            ObjectHideFlags = 0,
            Name = shader.Name,
            Platforms = shader.Platforms?.Select(static p => (uint)p).ToList() ?? [],
            CompressedBlob = shader.CompressedBlob ?? [],
        };

        metadata.ParsedForm.Name = parsedForm.Name_R;
        metadata.ParsedForm.FallbackName = parsedForm.FallbackName?.String ?? string.Empty;
        metadata.Offsets = ReadUInt32Matrix(shader.Offsets_AssetList_AssetList_UInt32, shader.Offsets_AssetList_UInt32);
        metadata.CompressedLengths = ReadUInt32Matrix(shader.CompressedLengths_AssetList_AssetList_UInt32, shader.CompressedLengths_AssetList_UInt32);
        metadata.DecompressedLengths = ReadUInt32Matrix(shader.DecompressedLengths_AssetList_AssetList_UInt32, shader.DecompressedLengths_AssetList_UInt32);

        if (parsedForm.Has_CustomEditorForRenderPipelines() && parsedForm.CustomEditorForRenderPipelines is not null)
        {
            metadata.ParsedForm.CustomEditorForRenderPipelines = parsedForm.CustomEditorForRenderPipelines.Select(static item => new UnitySerializedCustomEditorForRenderPipeline
            {
                CustomEditorName = item.CustomEditorName.String,
                RenderPipelineType = item.RenderPipelineType.String,
            }).ToList();
        }

        if (parsedForm.PropInfo is not null)
        {
            foreach (var property in parsedForm.PropInfo.Props)
            {
                metadata.ParsedForm.PropInfo.Props.Add(new UnitySerializedProperty
                {
                    Name = property.Name_R,
                    Description = property.Description,
                    Attributes = property.Attributes?.Select(static s => s.String).ToList() ?? [],
                    Type = (int)property.Type,
                    Flags = (uint)property.Flags,
                    DefValue = [property.DefValue_0_, property.DefValue_1_, property.DefValue_2_, property.DefValue_3_],
                    DefTexture = new UnitySerializedTextureProperty
                    {
                        DefaultName = property.DefTexture.DefaultName.String,
                        TexDim = (int)property.DefTexture.TexDim,
                    },
                });
            }
        }

        metadata.ParsedForm.KeywordNames = parsedForm.KeywordNames?.Select(static s => s.String).ToList() ?? [];

        for (int subShaderIndex = 0; subShaderIndex < parsedForm.SubShaders.Count; subShaderIndex++)
        {
            var sourceSubShader = parsedForm.SubShaders[subShaderIndex];
            UnitySerializedSubShader subShader = new()
            {
                LOD = sourceSubShader.LOD,
            };
            CopyTags(sourceSubShader.Tags, subShader.Tags.Tags);

            for (int passIndex = 0; passIndex < sourceSubShader.Passes.Count; passIndex++)
            {
                var sourcePass = sourceSubShader.Passes[passIndex];
                UnitySerializedPass pass = BuildPass(sourcePass, shader.Collection.Version, platform, enumerateProgramBlobIndices);
                subShader.Passes.Add(pass);
            }

            metadata.ParsedForm.SubShaders.Add(subShader);
        }

        return metadata;
    }

    public static void BackfillProgramSources(UnityShaderMetadata metadata, IReadOnlyList<ProgramResultLocation> locations, DecompileResult[] results)
    {
        for (int i = 0; i < locations.Count; i++)
        {
            ProgramResultLocation location = locations[i];
            DecompileResult result = results[i];

            UnitySerializedPass pass = metadata.ParsedForm.SubShaders[location.SubShaderIndex].Passes[location.PassIndex];
            UnitySerializedProgram? programSlot = pass.GetProgramSlot(location.Stage);
            if (programSlot is null)
            {
                continue;
            }

            UnitySerializedSubProgram? subProgram = programSlot.SubPrograms.FirstOrDefault(sp =>
                sp.BlobIndex == location.BlobIndex
                && sp.ParameterBlobIndex == location.ParameterBlobIndex
                && sp.KeywordIndices.SequenceEqual(location.KeywordIndices));
            if (subProgram is null)
            {
                continue;
            }

            subProgram.Success = result.Success;
            subProgram.SourceCode = result.SourceCode;
            subProgram.SourceLanguage = result.SourceLanguage;
            subProgram.SourceFileExtension = result.SourceFileExtension;
            subProgram.ErrorMessage = result.ErrorMessage;
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
                First = pair.Key.ToString(),
                Second = pair.Value,
            });
        }

        UnitySerializedPass pass = new()
        {
            Type = (int)sourcePass.Type,
            UseName = sourcePass.UseName,
            Name = sourcePass.Name,
            TextureName = sourcePass.TextureName,
            ProgramMask = sourcePass.ProgramMask,
            HasInstancingVariant = sourcePass.HasInstancingVariant,
            HasProceduralInstancingVariant = sourcePass.Has_HasProceduralInstancingVariant() && sourcePass.HasProceduralInstancingVariant,
            NameIndices = nameIndices,
            State = new UnitySerializedShaderState
            {
                Name = sourcePass.State.Name_R,
                GpuProgramID = sourcePass.State.GpuProgramID,
                LOD = sourcePass.State.LOD,
            },
        };

        if (!string.IsNullOrWhiteSpace(pass.UseName))
        {
            return pass;
        }

        BuildPassState(sourcePass.State, pass);
        AssignProgramSlot(pass, "Vertex", sourcePass.ProgVertex, version, platform, enumerateProgramBlobIndices);
        AssignProgramSlot(pass, "Fragment", sourcePass.ProgFragment, version, platform, enumerateProgramBlobIndices);
        AssignProgramSlot(pass, "Geometry", sourcePass.ProgGeometry, version, platform, enumerateProgramBlobIndices);
        AssignProgramSlot(pass, "Hull", sourcePass.ProgHull, version, platform, enumerateProgramBlobIndices);
        AssignProgramSlot(pass, "Domain", sourcePass.ProgDomain, version, platform, enumerateProgramBlobIndices);
        AssignProgramSlot(pass, "RayTracing", sourcePass.ProgRayTracing, version, platform, enumerateProgramBlobIndices);
        return pass;
    }

    // Build the SerializedProgram for one stage slot and attach it to the pass. The
    // SubPrograms list is populated from `enumerateProgramBlobIndices` (which already
    // does the per-platform filtering and dedupes the (Blob, ParamBlob, Keyword) tuples
    // across SubPrograms + PlayerSubPrograms). PlayerSubPrograms / ParameterBlobIndices /
    // CommonParameters are intentionally left empty: they're Unity-wire round-trip
    // fields, and the Writer / decompiler only consume `SubPrograms`. Decompile output
    // (Success / SourceCode / ErrorMessage) is filled in later by BackfillProgramSources.
    private static void AssignProgramSlot(
        UnitySerializedPass pass,
        string stage,
        ISerializedProgram? sourceProgram,
        UnityVersion version,
        GPUPlatform platform,
        Func<ISerializedProgram, UnityVersion, GPUPlatform, IEnumerable<ProgramBlobReference>> enumerateProgramBlobIndices)
    {
        if (sourceProgram is null)
        {
            return;
        }

        UnitySerializedProgram program = new();
        foreach (ProgramBlobReference blob in enumerateProgramBlobIndices(sourceProgram, version, platform))
        {
            program.SubPrograms.Add(new UnitySerializedSubProgram
            {
                BlobIndex = blob.BlobIndex,
                ParameterBlobIndex = blob.ParameterBlobIndex,
                KeywordIndices = blob.KeywordIndices,
            });
        }

        if (program.SubPrograms.Count == 0)
        {
            return;
        }

        pass.SetProgramSlot(stage, program);
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
        pass.State.RtSeparateBlend = state.RtSeparateBlend;
        pass.State.RtBlend0 = BuildRtBlendState(state.RtBlend0);
        pass.State.RtBlend1 = BuildRtBlendState(state.RtBlend1);
        pass.State.RtBlend2 = BuildRtBlendState(state.RtBlend2);
        pass.State.RtBlend3 = BuildRtBlendState(state.RtBlend3);
        pass.State.RtBlend4 = BuildRtBlendState(state.RtBlend4);
        pass.State.RtBlend5 = BuildRtBlendState(state.RtBlend5);
        pass.State.RtBlend6 = BuildRtBlendState(state.RtBlend6);
        pass.State.RtBlend7 = BuildRtBlendState(state.RtBlend7);
        pass.State.StencilOp = BuildStencilOp(state.StencilOp);
        pass.State.StencilOpFront = BuildStencilOp(state.StencilOpFront);
        pass.State.StencilOpBack = BuildStencilOp(state.StencilOpBack);
        pass.State.StencilRef = BuildFloatValue(state.StencilRef.Value, GetFloatValueName(state.StencilRef));
        pass.State.StencilReadMask = BuildFloatValue(state.StencilReadMask.Value, GetFloatValueName(state.StencilReadMask));
        pass.State.StencilWriteMask = BuildFloatValue(state.StencilWriteMask.Value, GetFloatValueName(state.StencilWriteMask));
        pass.State.FogMode = (int)state.FogMode;
        pass.State.FogColor = BuildVectorValue(state.FogColor);
        pass.State.FogDensity = BuildFloatValue(state.FogDensity.Value, GetFloatValueName(state.FogDensity));
        pass.State.FogStart = BuildFloatValue(state.FogStart.Value, GetFloatValueName(state.FogStart));
        pass.State.FogEnd = BuildFloatValue(state.FogEnd.Value, GetFloatValueName(state.FogEnd));
        pass.State.AlphaToMask = BuildFloatValue(state.AlphaToMask.Value, GetFloatValueName(state.AlphaToMask));
        pass.State.ZClip = state.Has_ZClip() ? BuildFloatValue(state.ZClip!.Value, GetFloatValueName(state.ZClip)) : new UnitySerializedShaderFloatValue();
        pass.State.ZTest = BuildFloatValue(state.ZTest.Value, GetFloatValueName(state.ZTest));
        pass.State.ZWrite = BuildFloatValue(state.ZWrite.Value, GetFloatValueName(state.ZWrite));
        pass.State.Culling = BuildFloatValue(state.Culling.Value, GetFloatValueName(state.Culling));
        pass.State.OffsetFactor = BuildFloatValue(state.OffsetFactor.Value, GetFloatValueName(state.OffsetFactor));
        pass.State.OffsetUnits = BuildFloatValue(state.OffsetUnits.Value, GetFloatValueName(state.OffsetUnits));
        pass.State.Lighting = state.Lighting;
        CopyTags(state.Tags, pass.State.Tags.Tags);
    }

    private static UnitySerializedShaderRTBlendState BuildRtBlendState(ISerializedShaderRTBlendState state)
    {
        return new UnitySerializedShaderRTBlendState
        {
            SrcBlend = BuildFloatValue(state.SourceBlend.Value, GetFloatValueName(state.SourceBlend)),
            DestBlend = BuildFloatValue(state.DestinationBlend.Value, GetFloatValueName(state.DestinationBlend)),
            SrcBlendAlpha = BuildFloatValue(state.SourceBlendAlpha.Value, GetFloatValueName(state.SourceBlendAlpha)),
            DestBlendAlpha = BuildFloatValue(state.DestinationBlendAlpha.Value, GetFloatValueName(state.DestinationBlendAlpha)),
            BlendOp = BuildFloatValue(state.BlendOp.Value, GetFloatValueName(state.BlendOp)),
            BlendOpAlpha = BuildFloatValue(state.BlendOpAlpha.Value, GetFloatValueName(state.BlendOpAlpha)),
            ColMask = BuildFloatValue(state.ColMask.Value, GetFloatValueName(state.ColMask)),
        };
    }

    private static UnitySerializedStencilOp BuildStencilOp(ISerializedStencilOp state)
    {
        return new UnitySerializedStencilOp
        {
            Pass = BuildFloatValue(state.Pass.Value, GetFloatValueName(state.Pass)),
            Fail = BuildFloatValue(state.Fail.Value, GetFloatValueName(state.Fail)),
            ZFail = BuildFloatValue(state.ZFail.Value, GetFloatValueName(state.ZFail)),
            Comp = BuildFloatValue(state.Comp.Value, GetFloatValueName(state.Comp)),
        };
    }

    private static UnitySerializedShaderVectorValue BuildVectorValue(ISerializedShaderVectorValue value)
    {
        return new UnitySerializedShaderVectorValue
        {
            X = BuildFloatValue(value.X.Value, GetFloatValueName(value.X)),
            Y = BuildFloatValue(value.Y.Value, GetFloatValueName(value.Y)),
            Z = BuildFloatValue(value.Z.Value, GetFloatValueName(value.Z)),
            W = BuildFloatValue(value.W.Value, GetFloatValueName(value.W)),
        };
    }

    private static UnitySerializedShaderFloatValue BuildFloatValue(float value, string name)
    {
        return new UnitySerializedShaderFloatValue
        {
            Val = value,
            Name = name,
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
                First = tag.Key.String,
                Second = tag.Value.String,
            });
        }
    }
}
