using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The textures a material's uniform buffer holds, in the order it holds them -- which is the
/// order a shader's texture bindings count along, and so the only way a binding index becomes a
/// name. The kind each one is (2D, cube, array, volume, virtual) is the bucket it sits in.
/// </summary>
internal static class MaterialTextureOrder
{
    public const int Standard2DBucket = 0;
    public const int CubeBucket = 1;
    public const int Array2DBucket = 2;
    public const int ArrayCubeBucket = 3;
    public const int VolumeBucket = 4;
    public const int VirtualBucket = 5;

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
            for (int slot = 0; slot < bucket.Length; slot++)
            {
                names.Add(PreshaderInputs.NameOf(bucket[slot]) ?? Generated(bucketIndex, slot));
                bucketIndices.Add(bucketIndex);
            }
        }
        return names;
    }

    /// <summary>
    /// What the engine's own material translator calls a texture the material does NOT expose as
    /// a parameter -- one wired straight into the graph, which has no parameter name to take.
    /// The translator numbers those by their place in their bucket, and that is the name the
    /// cooked shader binds them under: measured across one shipped title, every such binding it
    /// names is spelled <c>Material_Texture2D_&lt;slot&gt;</c>, slot counted within the 2D bucket.
    /// </summary>
    private static string Generated(int bucket, int slot) => bucket switch
    {
        Standard2DBucket => $"Texture2D_{slot}",
        CubeBucket => $"TextureCube_{slot}",
        Array2DBucket => $"Texture2DArray_{slot}",
        ArrayCubeBucket => $"TextureCubeArray_{slot}",
        VolumeBucket => $"VolumeTexture_{slot}",
        VirtualBucket => $"VirtualTexture_{slot}",
        _ => $"Texture_{slot}",
    };
}
