using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Math;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// One numeric parameter a preshader program can read: what it is called, what the material
/// cooked as its value, how many lanes of that value mean anything, and which kind of knob the
/// material exposes it as -- which is what a host writes it back out as.
/// </summary>
public readonly record struct NumericParameter(string Name, float[]? Value, int Components, EMaterialParameterType Kind);

/// <summary>
/// Everything a material's preshader programs run on: the opcode bytes, the parameters they
/// read, and the names they can label a texture with.
///
/// Stated as this and not as the expression set it came from, so the evaluator knows nothing
/// about materials, about how a build serialises one, or about which engine version wrote it.
/// That is also what makes an instance's overrides expressible: a material instance states a
/// value for a parameter by name, which is one array with some lanes replaced.
/// </summary>
public sealed class PreshaderInputs
{
    private static readonly IReadOnlyList<IReadOnlyList<string>> NoTextures = Array.Empty<IReadOnlyList<string>>();

    private PreshaderInputs(byte[] opcodes, NumericParameter[] numeric, string[] names, IReadOnlyList<IReadOnlyList<string>> textureNames)
    {
        Opcodes = opcodes;
        Numeric = numeric;
        Names = names;
        TextureNames = textureNames;
    }

    public byte[] Opcodes { get; }

    public NumericParameter[] Numeric { get; }

    /// <summary>The names the preshader data carries, which a texture opcode labels its slot with.</summary>
    public string[] Names { get; }

    /// <summary>Texture parameter names by the bucket the expression set groups them in, then by slot.</summary>
    public IReadOnlyList<IReadOnlyList<string>> TextureNames { get; }

    /// <summary>
    /// The inputs a material's programs run on, whichever way its engine wrote the parameters
    /// down. Both dialects are normalised in one place -- <see cref="MaterialExpressions"/> --
    /// so nothing here knows that a pre-UE5 cook keeps vectors and scalars in two tables.
    /// </summary>
    public static PreshaderInputs? Of(FUniformExpressionSet? expressionSet)
    {
        if (MaterialExpressions.Of(expressionSet) is not { } expressions)
        {
            return null;
        }
        return new PreshaderInputs(expressions.Opcodes, expressions.Parameters, expressions.Names, expressions.TextureNames);
    }

    /// <summary>The same inputs with every parameter the caller states replaced by the value it states.</summary>
    public PreshaderInputs With(IReadOnlyDictionary<string, float[]> stated)
    {
        NumericParameter[] patched = new NumericParameter[Numeric.Length];
        for (int i = 0; i < patched.Length; i++)
        {
            NumericParameter parameter = Numeric[i];
            patched[i] = stated.TryGetValue(parameter.Name, out float[]? value)
                ? parameter with { Value = Lanes(value, parameter.Components == 1) }
                : parameter;
        }
        return new PreshaderInputs(Opcodes, patched, Names, TextureNames);
    }

    public static NumericParameter[] Parameters(FMaterialNumericParameterInfo[]? parameters)
    {
        if (parameters is null)
        {
            return [];
        }
        NumericParameter[] result = new NumericParameter[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            FMaterialNumericParameterInfo parameter = parameters[i];
            bool scalar = parameter.ParameterType == EMaterialParameterType.Scalar;
            result[i] = new NumericParameter(
                parameter.ParameterInfo?.Name.Text ?? string.Empty,
                Value(parameter.Value, scalar),
                scalar ? 1 : Components(parameter.Value),
                parameter.ParameterType);
        }
        return result;
    }

    /// <summary>Texture names as the expression set groups them: one list per kind, in slot order.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> TextureBuckets(FMaterialTextureParameterInfo[][]? buckets)
    {
        if (buckets is null)
        {
            return NoTextures;
        }
        List<IReadOnlyList<string>> result = new(buckets.Length);
        foreach (FMaterialTextureParameterInfo[]? bucket in buckets)
        {
            List<string> names = new(bucket?.Length ?? 0);
            foreach (FMaterialTextureParameterInfo parameter in bucket ?? [])
            {
                names.Add(NameOf(parameter) ?? string.Empty);
            }
            result.Add(names);
        }
        return result;
    }

    /// <summary>What a texture parameter is called, as the build spells it in whichever of its two parameter-info shapes.</summary>
    public static string? NameOf(FMaterialBaseParameterInfo parameter)
    {
        string? name = parameter.ParameterInfo is { } info ? info.Name.Text
            : parameter.ParameterInfoOld is { } old ? old.Name.ToString()
            : parameter.ParameterName;
        return string.IsNullOrWhiteSpace(name) || name == "None" ? null : name;
    }

    private static float[]? Value(object? value, bool scalar) => value switch
    {
        null => null,
        float single => [single, single, single, single],
        double wide => Lanes([(float)wide], scalar),
        FLinearColor color => Lanes([color.R, color.G, color.B, color.A], scalar),
        FVector4 vector => Lanes([(float)vector.X, (float)vector.Y, (float)vector.Z, (float)vector.W], scalar),
        _ => null,
    };

    private static int Components(object? value) => value switch
    {
        float => 1,
        double => 1,
        _ => 4,
    };

    private static float[] Lanes(float[] stated, bool scalar)
    {
        float[] lanes = new float[4];
        for (int i = 0; i < lanes.Length; i++)
        {
            lanes[i] = scalar ? stated[0] : i < stated.Length ? stated[i] : 0f;
        }
        return lanes;
    }
}
