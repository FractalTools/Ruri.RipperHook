using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The textures a material's uniform buffer holds, in the order it holds them -- which is the
/// order a shader's texture bindings count along, and so the only way a binding index becomes a
/// name. The kind each one is (2D, cube, array, volume, virtual) is the bucket it sits in.
/// </summary>
internal static class MaterialTextureOrder
{
    public static List<string> Extract(FUniformExpressionSet uniformExpressionSet)
    {
        return Extract(uniformExpressionSet, out _);
    }

    public static List<string> Extract(FUniformExpressionSet uniformExpressionSet, out List<int> bucketIndices)
    {
        var names = new List<string>();
        bucketIndices = new List<int>();
        if (uniformExpressionSet.UniformTextureParameters is not { } buckets)
        {
            return names;
        }

        int bucketIndex = -1;
        foreach (FMaterialTextureParameterInfo[]? bucket in buckets)
        {
            if (bucket is null)
            {
                continue;
            }
            bucketIndex++;
            foreach (FMaterialTextureParameterInfo parameter in bucket)
            {
                names.Add(PreshaderInputs.NameOf(parameter) ?? $"Texture_{names.Count}");
                bucketIndices.Add(bucketIndex);
            }
        }
        return names;
    }
}
