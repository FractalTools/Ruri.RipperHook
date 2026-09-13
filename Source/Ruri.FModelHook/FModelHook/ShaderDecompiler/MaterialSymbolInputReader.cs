using CUE4Parse.UE4.Assets.Exports.Material;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// What a material's compiled expression set says its shaders' symbols are: the constant buffer
/// the material fills, the numeric parameters that fill it, and how many resources of each kind
/// its uniform buffer holds -- which is what tells a binding index which texture it is.
/// </summary>
internal static class SymbolInputsReader
{
    public static SymbolInputs? ReadFromUniformExpressionSet(string materialPath, string? shaderPlatform, FUniformExpressionSet uniformExpressionSet)
    {
        SymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
            UsedLoadedMaterialResources = true,
        };

        inputs.MaterialConstantBuffer = MaterialConstantBufferReader.Read(uniformExpressionSet, inputs.MaterialPath);
        foreach (FMaterialNumericParameterInfo parameter in uniformExpressionSet.UniformNumericParameters ?? [])
        {
            if (Info(parameter.ParameterInfo) is { } info)
            {
                inputs.NumericParameterInfos.Add(info);
            }
        }
        inputs.MaterialResourceCounts = ResourceCounts(uniformExpressionSet);

        return inputs.NumericParameterInfos.Count == 0
               && inputs.MaterialConstantBuffer == null
               && inputs.MaterialResourceCounts == null
            ? null
            : inputs;
    }

    /// <summary>
    /// What this material states about its own uniform buffer: how many of each kind of texture
    /// it holds and what it calls them, bucket by bucket AS THE COOK WROTE THEM. Which kind each
    /// bucket is stays the engine's business -- this side only counts.
    /// </summary>
    private static MaterialUniformBufferLayout.MaterialResources? ResourceCounts(FUniformExpressionSet uniformExpressionSet)
    {
        FMaterialTextureParameterInfo[][]? buckets = uniformExpressionSet.UniformTextureParameters;
        if (buckets is null)
        {
            return null;
        }

        List<int> byBucket = new(buckets.Length);
        List<IReadOnlyList<string?>> namesByBucket = new(buckets.Length);
        for (int bucket = 0; bucket < buckets.Length; bucket++)
        {
            FMaterialTextureParameterInfo[]? held = buckets[bucket];
            byBucket.Add(held?.Length ?? 0);
            List<string?> names = new(held?.Length ?? 0);
            foreach (FMaterialTextureParameterInfo parameter in held ?? [])
            {
                names.Add(PreshaderInputs.NameOf(parameter));
            }
            namesByBucket.Add(names);
        }

        FMaterialExternalTextureParameterInfo[]? externalParameters = uniformExpressionSet.UniformExternalTextureParameters;
        List<int> virtualTextureStackLayers = new();
        foreach (FMaterialVirtualTextureStack stack in uniformExpressionSet.VTStacks ?? [])
        {
            virtualTextureStackLayers.Add((int)stack.NumLayers);
        }

        MaterialUniformBufferRecipe.Counts counts = new(
            TexturesByBucket: byBucket,
            ExternalTextures: externalParameters?.Length ?? 0,
            TextureCollections: uniformExpressionSet.UniformTextureCollectionParameters?.Length ?? 0,
            VirtualTextureStackLayers: virtualTextureStackLayers,
            VectorPreshaders: uniformExpressionSet.UniformVectorPreshaders?.Length ?? 0,
            ScalarPreshaders: uniformExpressionSet.UniformScalarPreshaders?.Length ?? 0,
            PreshaderBufferSize: (int)uniformExpressionSet.UniformPreshaderBufferSize);

        return new MaterialUniformBufferLayout.MaterialResources(
            counts, namesByBucket, ExternalAuthorNames(externalParameters));
    }


    private static IReadOnlyList<string?>? ExternalAuthorNames(FMaterialExternalTextureParameterInfo[]? parameters)
    {
        if (parameters is null)
        {
            return null;
        }
        List<string?> names = new(parameters.Length);
        foreach (FMaterialExternalTextureParameterInfo parameter in parameters)
        {
            string? name = parameter.ParameterName.Text;
            names.Add(string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase) ? null : name);
        }
        return names;
    }

    private static FMaterialParameterInfo? Info(FMemoryImageMaterialParameterInfo? parameterInfo)
    {
        string? name = parameterInfo?.Name.Text;
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase)
            ? null
            : new FMaterialParameterInfo(name!, parameterInfo!.Association, parameterInfo.Index);
    }
}

internal static class MaterialSymbolMetadataBuilder
{
    public static SerializedProgramData Build(SymbolInputs inputs)
    {
        SerializedProgramData metadata = new()
        {
            DebugName = inputs.MaterialPath
        };

        if (inputs.MaterialConstantBuffer != null)
        {
            metadata.ConstantBufferParameters.Add(inputs.MaterialConstantBuffer);
        }

        foreach (ConstantBufferParameter extra in inputs.ExtraConstantBuffers)
        {
            metadata.ConstantBufferParameters.Add(extra);
        }

        metadata.ConstantBufferParameters = metadata.ConstantBufferParameters
            .GroupBy(static buffer => buffer.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
        return metadata;
    }
}
