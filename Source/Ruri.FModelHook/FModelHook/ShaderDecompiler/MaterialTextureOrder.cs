using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The textures a material's uniform buffer holds, in the order it holds them -- which is the
/// order a shader's texture bindings count along, and so the only way a binding index becomes a
/// name. Which KIND each bucket is, and what the engine calls a texture the material exposes no
/// name for, are both the engine's own facts and are asked of
/// <see cref="MaterialUniformBufferRecipe"/> rather than written down here: 4.26 knows five kinds
/// and 5.5 seven, in different positions, and naming them here got every pre-UE5 build's volume
/// and virtual textures wrong.
/// </summary>
internal static class MaterialTextureOrder
{
    /// <summary>The kind of texture parameter a bucket holds, as the mounted engine numbers its buckets.</summary>
    public static string KindOf(int bucket)
    {
        IReadOnlyList<string> kinds = MaterialUniformBufferRecipe.Current.TextureKinds;
        return bucket >= 0 && bucket < kinds.Count ? kinds[bucket] : string.Empty;
    }

    /// <summary>Which bucket holds a kind for the mounted engine, or -1 where it has no such kind.</summary>
    public static int BucketOf(string kind) => MaterialUniformBufferRecipe.Current.BucketOf(kind);

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

        // Counted by POSITION, not by how many buckets happened to be filled: a kind this
        // material uses none of is still a bucket, and skipping it would renumber every kind
        // after it -- which is the same mistake as naming the buckets by hand.
        for (int bucketIndex = 0; bucketIndex < buckets.Length; bucketIndex++)
        {
            FMaterialTextureParameterInfo[]? bucket = buckets[bucketIndex];
            for (int slot = 0; slot < (bucket?.Length ?? 0); slot++)
            {
                names.Add(PreshaderInputs.NameOf(bucket![slot])
                          ?? MaterialUniformBufferRecipe.Current.GeneratedTextureName(bucketIndex, slot));
                bucketIndices.Add(bucketIndex);
            }
        }
        return names;
    }
}
