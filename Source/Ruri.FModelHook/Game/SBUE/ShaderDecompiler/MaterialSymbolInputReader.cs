using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class SymbolInputsReader
{
    public static SymbolInputs? Read(string materialPath, string? shaderPlatform, JsonElement asset)
    {
        SymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
        };

        JsonElement? selectedLoadedResource = SelectLoadedMaterialResource(asset, shaderPlatform, ref inputs);
        JsonElement? uniformExpressionSet = ResolveUniformExpressionSet(selectedLoadedResource);
        if (uniformExpressionSet.HasValue)
        {
            ReadUniformExpressionSet(inputs, uniformExpressionSet.Value);
        }

        ReadFallbackNumericParameters(asset, inputs.NumericParameterInfos);
        return inputs.NumericParameterInfos.Count == 0 && inputs.MaterialConstantBuffer == null
            ? null
            : inputs;
    }

    public static SymbolInputs? ReadFromUniformExpressionSet(string materialPath, string? shaderPlatform, JsonElement uniformExpressionSet)
    {
        SymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
            UsedLoadedMaterialResources = true,
        };

        ReadUniformExpressionSet(inputs, uniformExpressionSet);
        return inputs.NumericParameterInfos.Count == 0
               && inputs.MaterialConstantBuffer == null
               && inputs.MaterialResourceCounts == null
            ? null
            : inputs;
    }

    private static void ReadUniformExpressionSet(SymbolInputs inputs, JsonElement uniformExpressionSet)
    {
        inputs.MaterialConstantBuffer = MaterialConstantBufferReader.Read(uniformExpressionSet, inputs.MaterialPath);
        ReadUniformNumericParameters(uniformExpressionSet, inputs.NumericParameterInfos);
        inputs.MaterialResourceCounts = ReadMaterialResourceCounts(uniformExpressionSet);
    }

    private static JsonElement? SelectLoadedMaterialResource(JsonElement asset, string? shaderPlatform, ref SymbolInputs inputs)
    {
        if (!asset.TryGetProperty("LoadedMaterialResources", out JsonElement loadedResources) || loadedResources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) || loadedShaderMap.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? candidateShaderPlatform = ReadString(loadedShaderMap, "ShaderPlatform");
            if (!string.IsNullOrWhiteSpace(shaderPlatform) && !string.Equals(candidateShaderPlatform, shaderPlatform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            inputs.UsedLoadedMaterialResources = true;
            return resource.Clone();
        }

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            inputs.UsedLoadedMaterialResources = true;
            return resource.Clone();
        }

        return null;
    }

    private static JsonElement? ResolveUniformExpressionSet(JsonElement? loadedResource)
    {
        if (!loadedResource.HasValue)
        {
            return null;
        }

        JsonElement resource = loadedResource.Value;
        if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) || loadedShaderMap.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (loadedShaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement materialShaderMapContent)
            && materialShaderMapContent.ValueKind == JsonValueKind.Object
            && materialShaderMapContent.TryGetProperty("UniformExpressionSet", out JsonElement uniformExpressionSet))
        {
            return uniformExpressionSet.Clone();
        }

        if (loadedShaderMap.TryGetProperty("Content", out JsonElement content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("MaterialCompilationOutput", out JsonElement materialCompilationOutput)
            && materialCompilationOutput.ValueKind == JsonValueKind.Object
            && materialCompilationOutput.TryGetProperty("UniformExpressionSet", out JsonElement nestedUniformExpressionSet))
        {
            return nestedUniformExpressionSet.Clone();
        }

        return null;
    }

    private static MaterialUniformBufferLayout.MaterialResourceCounts? ReadMaterialResourceCounts(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement textureParams) || textureParams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int standard2D = ReadTypedArrayLength(textureParams, 0);
        int cube = ReadTypedArrayLength(textureParams, 1);
        int array2D = ReadTypedArrayLength(textureParams, 2);
        int arrayCube = ReadTypedArrayLength(textureParams, 3);
        int volume = ReadTypedArrayLength(textureParams, 4);
        int virtualCount = ReadTypedArrayLength(textureParams, 5);

        int external = 0;
        if (uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement externalParams) && externalParams.ValueKind == JsonValueKind.Array)
        {
            external = externalParams.GetArrayLength();
        }

        List<int>? vtStackLayers = null;
        if (uniformExpressionSet.TryGetProperty("VTStacks", out JsonElement vtStacks) && vtStacks.ValueKind == JsonValueKind.Array)
        {
            vtStackLayers = new List<int>(vtStacks.GetArrayLength());
            foreach (JsonElement stack in vtStacks.EnumerateArray())
            {
                vtStackLayers.Add(ReadVirtualTextureStackNumLayers(stack));
            }
        }

        int? totalResources = null;
        if (uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement ubl)
            && ubl.ValueKind == JsonValueKind.Object
            && ubl.TryGetProperty("Resources", out JsonElement resources)
            && resources.ValueKind == JsonValueKind.Array)
        {
            totalResources = resources.GetArrayLength();
        }

        IReadOnlyList<string?>? std2dNames = ReadTextureAuthorNames(textureParams, 0);
        IReadOnlyList<string?>? cubeNames = ReadTextureAuthorNames(textureParams, 1);
        IReadOnlyList<string?>? a2dNames = ReadTextureAuthorNames(textureParams, 2);
        IReadOnlyList<string?>? acubeNames = ReadTextureAuthorNames(textureParams, 3);
        IReadOnlyList<string?>? volNames = ReadTextureAuthorNames(textureParams, 4);
        IReadOnlyList<string?>? virtNames = ReadTextureAuthorNames(textureParams, 5);
        IReadOnlyList<string?>? extNames = ReadExternalAuthorNames(uniformExpressionSet);

        return new MaterialUniformBufferLayout.MaterialResourceCounts(
            Standard2D: standard2D,
            Cube: cube,
            Array2D: array2D,
            ArrayCube: arrayCube,
            Volume: volume,
            External: external,
            Virtual: virtualCount,
            VirtualTextureStackLayerCounts: vtStackLayers,
            TotalResourceCount: totalResources,
            Standard2DAuthorNames: std2dNames,
            CubeAuthorNames: cubeNames,
            Array2DAuthorNames: a2dNames,
            ArrayCubeAuthorNames: acubeNames,
            VolumeAuthorNames: volNames,
            ExternalAuthorNames: extNames,
            VirtualAuthorNames: virtNames);
    }

    private static IReadOnlyList<string?>? ReadTextureAuthorNames(JsonElement arrayOfArrays, int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= arrayOfArrays.GetArrayLength())
        {
            return null;
        }

        JsonElement inner = arrayOfArrays[typeIndex];
        if (inner.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string?> names = new(inner.GetArrayLength());
        foreach (JsonElement entry in inner.EnumerateArray())
        {
            FMaterialParameterInfo? info = ParseMaterialParameterInfo(entry);
            names.Add(info?.Name);
        }
        return names;
    }

    private static IReadOnlyList<string?>? ReadExternalAuthorNames(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement external)
            || external.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string?> names = new(external.GetArrayLength());
        foreach (JsonElement entry in external.EnumerateArray())
        {
            FMaterialParameterInfo? info = ParseMaterialParameterInfo(entry);
            names.Add(info?.Name);
        }
        return names;
    }

    private static int ReadVirtualTextureStackNumLayers(JsonElement stack)
    {
        if (stack.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (stack.TryGetProperty("NumLayers", out JsonElement numLayers) && numLayers.ValueKind == JsonValueKind.Number)
        {
            return numLayers.GetInt32();
        }

        if (stack.TryGetProperty("LayerUniformExpressionIndices", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (JsonElement element in layers.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number && element.GetInt32() >= 0)
                {
                    count++;
                }
            }
            return count;
        }

        return 0;
    }

    private static int ReadTypedArrayLength(JsonElement arrayOfArrays, int index)
    {
        if (index < 0 || index >= arrayOfArrays.GetArrayLength())
        {
            return 0;
        }

        JsonElement inner = arrayOfArrays[index];
        return inner.ValueKind == JsonValueKind.Array ? inner.GetArrayLength() : 0;
    }

    private static void ReadUniformNumericParameters(JsonElement uniformExpressionSet, List<FMaterialParameterInfo> destination)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement numericParameters) || numericParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement parameter in numericParameters.EnumerateArray())
        {
            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(parameter);
            if (parameterInfo != null)
            {
                destination.Add(parameterInfo);
            }
        }
    }

    private static void ReadFallbackNumericParameters(JsonElement asset, List<FMaterialParameterInfo> destination)
    {
        if (!asset.TryGetProperty("Properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        AppendMaterialParameterInfos(properties, "ScalarParameterValues", destination);
        AppendMaterialParameterInfos(properties, "VectorParameterValues", destination);
        AppendMaterialParameterInfos(properties, "DoubleVectorParameterValues", destination);
    }

    private static void AppendMaterialParameterInfos(JsonElement properties, string propertyName, List<FMaterialParameterInfo> destination)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(entry);
            if (parameterInfo != null)
            {
                destination.Add(parameterInfo);
            }
        }
    }

    private static FMaterialParameterInfo? ParseMaterialParameterInfo(JsonElement element)
    {
        JsonElement parameterInfo;
        bool nested;
        if (element.TryGetProperty("ParameterInfo", out parameterInfo) && parameterInfo.ValueKind == JsonValueKind.Object)
        {
            nested = true;
        }
        else
        {
            parameterInfo = element;
            nested = false;
        }

        string? name = nested
            ? ReadString(parameterInfo, "Name")
            : ReadString(parameterInfo, "ParameterName") ?? ReadString(parameterInfo, "Name");
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? associationRaw = ReadString(parameterInfo, "Association");
        EMaterialParameterAssociation association = associationRaw switch
        {
            "EMaterialParameterAssociation::LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "EMaterialParameterAssociation::BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            "LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            _ => EMaterialParameterAssociation.GlobalParameter
        };

        int index = parameterInfo.TryGetProperty("Index", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : -1;
        return new FMaterialParameterInfo(name, association, index);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
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

        // Append the resolved `MaterialCollection<i>` cbuffers so the rewriter
        // recognises them and emits named members instead of `_m0[N]`.
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
