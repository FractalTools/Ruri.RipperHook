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
    private const int Standard2DBucket = 0;
    private const int CubeBucket = 1;
    private const int Array2DBucket = 2;
    private const int ArrayCubeBucket = 3;
    private const int VolumeBucket = 4;
    private const int VirtualBucket = 5;

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

    private static MaterialUniformBufferLayout.MaterialResourceCounts? ResourceCounts(FUniformExpressionSet uniformExpressionSet)
    {
        FMaterialTextureParameterInfo[][]? buckets = uniformExpressionSet.UniformTextureParameters;
        if (buckets is null)
        {
            return null;
        }

        FMaterialExternalTextureParameterInfo[]? externalParameters = uniformExpressionSet.UniformExternalTextureParameters;
        List<int>? virtualTextureStackLayers = null;
        if (uniformExpressionSet.VTStacks is { } stacks)
        {
            virtualTextureStackLayers = new List<int>(stacks.Length);
            foreach (FMaterialVirtualTextureStack stack in stacks)
            {
                virtualTextureStackLayers.Add((int)stack.NumLayers);
            }
        }

        return new MaterialUniformBufferLayout.MaterialResourceCounts(
            Standard2D: Count(buckets, Standard2DBucket),
            Cube: Count(buckets, CubeBucket),
            Array2D: Count(buckets, Array2DBucket),
            ArrayCube: Count(buckets, ArrayCubeBucket),
            Volume: Count(buckets, VolumeBucket),
            External: externalParameters?.Length ?? 0,
            Virtual: Count(buckets, VirtualBucket),
            VirtualTextureStackLayerCounts: virtualTextureStackLayers,
            TotalResourceCount: uniformExpressionSet.UniformBufferLayoutInitializer?.Resources?.Length,
            Standard2DAuthorNames: AuthorNames(buckets, Standard2DBucket),
            CubeAuthorNames: AuthorNames(buckets, CubeBucket),
            Array2DAuthorNames: AuthorNames(buckets, Array2DBucket),
            ArrayCubeAuthorNames: AuthorNames(buckets, ArrayCubeBucket),
            VolumeAuthorNames: AuthorNames(buckets, VolumeBucket),
            ExternalAuthorNames: ExternalAuthorNames(externalParameters),
            VirtualAuthorNames: AuthorNames(buckets, VirtualBucket));
    }

    private static int Count(FMaterialTextureParameterInfo[][] buckets, int bucket) =>
        bucket >= 0 && bucket < buckets.Length ? buckets[bucket]?.Length ?? 0 : 0;

    private static IReadOnlyList<string?>? AuthorNames(FMaterialTextureParameterInfo[][] buckets, int bucket)
    {
        if (bucket < 0 || bucket >= buckets.Length || buckets[bucket] is not { } parameters)
        {
            return null;
        }
        List<string?> names = new(parameters.Length);
        foreach (FMaterialTextureParameterInfo parameter in parameters)
        {
            names.Add(PreshaderInputs.NameOf(parameter));
        }
        return names;
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
