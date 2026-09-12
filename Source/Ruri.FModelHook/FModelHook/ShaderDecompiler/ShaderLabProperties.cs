using System.Globalization;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// The knobs a material exposes, written as a shaderlab Properties block, and the constant-buffer
/// members each preshader program fills. Both are read from the expression set the map itself
/// compiled from, so a map with no material behind it simply has none.
/// </summary>
internal static class ShaderLabProperties
{
    public static void Build(ShaderSourceState state)
    {
        int populated = 0;
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            if (map.UniformExpressions is not { } uniformExpressions)
            {
                continue;
            }

            string block = BuildBlock(uniformExpressions);
            if (!string.IsNullOrEmpty(block))
            {
                map.PropertiesBlock = block;
                populated++;
            }

            string asset = map.PrimaryAsset;
            map.MaterialTextureOrder = new List<string>(MaterialTextureOrder.Extract(uniformExpressions, out List<int> textureBuckets));
            map.MaterialTextureBuckets = textureBuckets;

            MaterialConstantBufferReader.Read(uniformExpressions, asset);
            if (MaterialConstantBufferReader.EvaluatedCbufferValues.TryGetValue(asset, out var values))
            {
                map.MaterialCbufferValues = new Dictionary<string, string>(values, StringComparer.Ordinal);
            }
            if (MaterialConstantBufferReader.EvaluatedCbufferOffsets.TryGetValue(asset, out var offsets))
            {
                map.MaterialCbufferOffsets = new Dictionary<string, int>(offsets, StringComparer.Ordinal);
            }
            if (MaterialConstantBufferReader.EvaluatedCbufferPrograms.TryGetValue(asset, out var programs))
            {
                map.MaterialCbufferPrograms = new Dictionary<string, string>(programs, StringComparer.Ordinal);
            }
            if (MaterialConstantBufferReader.EvaluatedCbufferParams.TryGetValue(asset, out var parameters))
            {
                map.MaterialCbufferParams = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
            }
        }

        state.Log($"    Properties: populated {populated}/{state.ShaderMaps.Count} shader-maps.");
    }

    private static string BuildBlock(FUniformExpressionSet uniformExpressions)
    {
        var lines = new List<string>();
        HashSet<string> emittedIds = new(StringComparer.Ordinal);

        foreach (FMaterialNumericParameterInfo parameter in uniformExpressions.UniformNumericParameters ?? [])
        {
            string? line = TryBuildNumeric(parameter, emittedIds);
            if (line != null) lines.Add(line);
        }

        // Every texture the material's uniform buffer holds, under the name its shaders bind
        // it by -- the SAME list the emitted source is named from. Registering only the ones
        // the material happens to expose as parameters left a shaderlab whose Properties named
        // one texture while its passes sampled eight, which is not a material anyone can use.
        List<string> textures = MaterialTextureOrder.Extract(uniformExpressions, out List<int> buckets);
        for (int i = 0; i < textures.Count; i++)
        {
            string? line = TryBuildTexture(textures[i], buckets[i], emittedIds);
            if (line != null) lines.Add(line);
        }

        if (lines.Count == 0) return string.Empty;

        StringBuilder sb = new();
        sb.AppendLine("Properties {");
        foreach (string line in lines.Distinct(StringComparer.Ordinal))
        {
            sb.Append("    ");
            sb.AppendLine(line);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string? TryBuildNumeric(FMaterialNumericParameterInfo parameter, HashSet<string> emittedIds)
    {
        string rawName = parameter.ParameterInfo?.Name.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawName) || string.Equals(rawName, "None", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(rawName, "SelectionColor", StringComparison.OrdinalIgnoreCase)) return null;

        string identifier = ToIdentifier(rawName);
        if (!emittedIds.Add(identifier)) return null;

        string display = EscapeDisplayName(rawName);
        switch (parameter.ParameterType)
        {
            case EMaterialParameterType.Scalar:
                return $"{identifier} (\"{display}\", Float) = {FormatFloat(ReadScalar(parameter.Value))}";
            case EMaterialParameterType.Vector:
                {
                    (double r, double g, double b, double a) = ReadVector(parameter.Value);
                    return $"{identifier} (\"{display}\", Color) = ({FormatFloat(r)}, {FormatFloat(g)}, {FormatFloat(b)}, {FormatFloat(a)})";
                }
            case EMaterialParameterType.DoubleVector:
                {
                    (double r, double g, double b, double a) = ReadVector(parameter.Value);
                    return $"{identifier} (\"{display}\", Vector) = ({FormatFloat(r)}, {FormatFloat(g)}, {FormatFloat(b)}, {FormatFloat(a)})";
                }
            case EMaterialParameterType.StaticSwitch:
                return $"[Toggle] {identifier} (\"{display}\", Float) = {(ReadScalar(parameter.Value) >= 0.5 ? 1 : 0)}";
            default:
                return null;
        }
    }

    private static string? TryBuildTexture(string rawName, int bucket, HashSet<string> emittedIds)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        string identifier = ToIdentifier(rawName);
        if (!emittedIds.Add(identifier)) return null;

        string shaderlabType = bucket switch
        {
            MaterialTextureOrder.Standard2DBucket => "2D",
            MaterialTextureOrder.CubeBucket => "Cube",
            MaterialTextureOrder.Array2DBucket => "2DArray",
            MaterialTextureOrder.ArrayCubeBucket => "CubeArray",
            MaterialTextureOrder.VolumeBucket => "3D",
            MaterialTextureOrder.VirtualBucket => "2D",
            _ => "2D",
        };
        string defaultLiteral = bucket switch
        {
            MaterialTextureOrder.Standard2DBucket or MaterialTextureOrder.VirtualBucket => "\"white\" {}",
            _ => "\"\" {}",
        };
        string display = EscapeDisplayName(rawName);
        return $"{identifier} (\"{display}\", {shaderlabType}) = {defaultLiteral}";
    }

    private static double ReadScalar(object? value) => value switch
    {
        float single => single,
        double wide => wide,
        CUE4Parse.UE4.Objects.Core.Math.FLinearColor color => color.R,
        CUE4Parse.UE4.Objects.Core.Math.FVector4 vector => vector.X,
        _ => 0.0,
    };

    private static (double R, double G, double B, double A) ReadVector(object? value) => value switch
    {
        CUE4Parse.UE4.Objects.Core.Math.FLinearColor color => (color.R, color.G, color.B, color.A),
        CUE4Parse.UE4.Objects.Core.Math.FVector4 vector => (vector.X, vector.Y, vector.Z, vector.W),
        float single => (single, 0, 0, 0),
        double wide => (wide, 0, 0, 0),
        _ => (0, 0, 0, 0),
    };

    private static string ToIdentifier(string raw)
    {
        StringBuilder sb = new(raw.Length + 1);
        sb.Append('_');
        foreach (char c in raw)
        {
            sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
        }
        StringBuilder collapsed = new(sb.Length);
        bool prevUnderscore = false;
        foreach (char c in sb.ToString())
        {
            if (c == '_')
            {
                if (!prevUnderscore) collapsed.Append('_');
                prevUnderscore = true;
            }
            else
            {
                collapsed.Append(c);
                prevUnderscore = false;
            }
        }
        string s = collapsed.ToString();
        return s.Length == 0 || s == "_" ? "_Param" : s;
    }

    private static string EscapeDisplayName(string raw)
        => raw.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatFloat(double value)
    {
        string s = value.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
            if (s.Length == 0 || s == "-") s = "0";
        }
        return s;
    }
}
