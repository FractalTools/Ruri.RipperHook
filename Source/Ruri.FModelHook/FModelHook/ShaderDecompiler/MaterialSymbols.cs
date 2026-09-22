using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>Where a shader's symbols came from and how much the material could say about them.</summary>
internal sealed record MaterialSymbolSource(
    string MaterialPath,
    SerializedProgramData Metadata,
    int Score,
    bool UsedLoadedMaterialResources,
    MaterialUniformBufferLayout? MaterialLayout);

/// <summary>
/// The names behind one shader map's bindings, read off the material that compiled it.
///
/// A compiled shader binds its material's constant buffer, its textures and its samplers by
/// index; the expression set the same material cooked states what each of those indices is. One
/// map, one material, one reading -- there is no search over candidates and no scoring, because
/// the map is reached from the material that owns it rather than the other way round.
/// </summary>
internal static class MaterialSymbols
{
    private const int ParentChainLimit = 8;

    public static MaterialSymbolSource? Of(ShaderMapInfo map)
    {
        if (map.UniformExpressions is not { } uniformExpressions)
        {
            return null;
        }
        SymbolInputs? inputs = SymbolInputsReader.ReadFromUniformExpressionSet(
            map.PrimaryAsset, map.Target.ShaderPlatform, uniformExpressions);
        if (inputs is null)
        {
            return null;
        }
        AppendParameterCollections(inputs, map.Target.Material ?? map.Target.Owner);

        SerializedProgramData built = MaterialSymbolMetadataBuilder.Build(inputs);
        foreach (string textureName in MaterialTextureOrder.Extract(uniformExpressions))
        {
            built.TextureParameters.Add(new TextureParameter
            {
                Name = textureName,
                NameIndex = -1,
                Index = built.TextureParameters.Count,
                SamplerIndex = -1,
                MultiSampled = false,
                Dim = 2,
            });
        }
        return new MaterialSymbolSource(
            map.PrimaryAsset,
            built,
            inputs.UsedLoadedMaterialResources ? 2 : inputs.NumericParameterInfos.Count > 0 ? 1 : 0,
            inputs.UsedLoadedMaterialResources,
            inputs.MaterialResourceCounts is { } counts ? new MaterialUniformBufferLayout(counts) : null);
    }

    /// <summary>
    /// The parameter collections a material draws on, as the constant buffers a shader binds them
    /// through. A collection is shared by every material that names it, so the material states
    /// which ones it uses and in what order, and each collection states its own members: the
    /// scalars packed four to a register first, then one register per vector.
    /// </summary>
    private static void AppendParameterCollections(SymbolInputs inputs, UMaterialInterface? material)
    {
        UObject? current = material;
        for (int hop = 0; current is not null && hop <= ParentChainLimit; hop++)
        {
            if (current.TryGetValue(out FStructFallback cached, "CachedExpressionData")
                && cached.TryGetValue(out FStructFallback[] infos, "ParameterCollectionInfos")
                && infos.Length > 0)
            {
                for (int index = 0; index < infos.Length; index++)
                {
                    if (!infos[index].TryGetValue(out UObject collection, "ParameterCollection"))
                    {
                        continue;
                    }
                    // The engine binds a collection under its index in the material's list. A
                    // build that binds it under the collection's own state id instead states the
                    // same collection by a name the shader spells differently; both spellings
                    // are offered and the shader takes whichever it declares.
                    inputs.ExtraConstantBuffers.Add(Buffer($"MaterialCollection{index}", collection));
                    if (infos[index].TryGetValue(out FGuid stateId, "StateId"))
                    {
                        inputs.ExtraConstantBuffers.Add(Buffer($"MaterialCollection{stateId.ToString(EGuidFormats.Digits)}", collection));
                    }
                }
                return;
            }
            current = current is UMaterialInstance instance ? instance.Parent : null;
        }
    }

    private static ConstantBufferParameter Buffer(string name, UObject collection)
    {
        string[] scalars = Names(collection, "ScalarParameters");
        string[] vectors = Names(collection, "VectorParameters");

        List<VectorParameter> members = new(scalars.Length + vectors.Length);
        for (int i = 0; i < scalars.Length; i++)
        {
            members.Add(new VectorParameter
            {
                Name = Identifier(scalars[i].Length > 0 ? scalars[i] : $"Scalar_{i}"),
                NameIndex = -1,
                Type = ShaderParamType.Float,
                Index = i / 4 * 16 + i % 4 * 4,
                ArraySize = 1,
                IsMatrix = false,
                RowCount = 1,
                ColumnCount = 1,
            });
        }
        int scalarRegisters = (scalars.Length + 3) / 4;
        for (int i = 0; i < vectors.Length; i++)
        {
            members.Add(new VectorParameter
            {
                Name = Identifier(vectors[i].Length > 0 ? vectors[i] : $"Vector_{i}"),
                NameIndex = -1,
                Type = ShaderParamType.Float,
                Index = (scalarRegisters + i) * 16,
                ArraySize = 1,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
        }
        return new ConstantBufferParameter
        {
            Name = name,
            NameIndex = -1,
            VectorParameters = members.ToArray(),
            MatrixParameters = Array.Empty<MatrixParameter>(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = (scalarRegisters + vectors.Length) * 16,
            IsPartialCB = false,
        };
    }

    private static string[] Names(UObject collection, string property)
    {
        if (!collection.TryGetValue(out FStructFallback[] entries, property))
        {
            return [];
        }
        string[] names = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            names[i] = entries[i].TryGetValue(out FName name, "ParameterName") ? name.Text ?? string.Empty : string.Empty;
        }
        return names;
    }

    private static string Identifier(string raw)
    {
        System.Text.StringBuilder builder = new(raw.Length);
        foreach (char character in raw)
        {
            bool valid = character is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';
            builder.Append(valid ? character : '_');
        }
        if (builder.Length > 0 && builder[0] is >= '0' and <= '9')
        {
            builder.Insert(0, '_');
        }
        string result = builder.ToString();
        while (result.Contains("__", StringComparison.Ordinal))
        {
            result = result.Replace("__", "_", StringComparison.Ordinal);
        }
        return result.Trim('_').Length == 0 ? "_" : result;
    }
}
