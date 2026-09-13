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
            MaterialConstantBufferReader.Read(uniformExpressions, asset);
            map.MaterialTextureOrder = new List<string>(MaterialTextureOrder.Extract(uniformExpressions, out List<int> textureBuckets));
            map.MaterialTextureBuckets = textureBuckets;
        }

        state.Log($"    Properties: populated {populated}/{state.ShaderMaps.Count} shader-maps.");
    }

    private static string BuildBlock(FUniformExpressionSet uniformExpressions)
    {
        var lines = new List<string>();
        HashSet<string> emittedIds = new(StringComparer.Ordinal);

        // Every numeric knob the material exposes, whichever way its engine wrote the table --
        // one table of typed parameters on UE5, two untyped ones on UE4. Reading only the first
        // shape registered not a single colour, vector or scalar for any pre-UE5 title, which is
        // a shaderlab whose Properties name eight textures and nothing a shader actually reads.
        foreach (NumericParameter parameter in MaterialExpressions.Of(uniformExpressions)?.Parameters ?? [])
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

    private static string? TryBuildNumeric(NumericParameter parameter, HashSet<string> emittedIds)
    {
        string rawName = parameter.Name;
        if (string.IsNullOrWhiteSpace(rawName) || string.Equals(rawName, "None", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(rawName, "SelectionColor", StringComparison.OrdinalIgnoreCase)) return null;

        string identifier = ToIdentifier(rawName);
        if (!emittedIds.Add(identifier)) return null;

        string display = EscapeDisplayName(rawName);
        float[] value = parameter.Value ?? [0f, 0f, 0f, 0f];
        switch (parameter.Kind)
        {
            case EMaterialParameterType.Scalar:
                return $"{identifier} (\"{display}\", Float) = {FormatFloat(value[0])}";
            case EMaterialParameterType.Vector:
                return $"{identifier} (\"{display}\", Color) = ({FormatFloat(value[0])}, {FormatFloat(value[1])}, {FormatFloat(value[2])}, {FormatFloat(value[3])})";
            case EMaterialParameterType.DoubleVector:
                return $"{identifier} (\"{display}\", Vector) = ({FormatFloat(value[0])}, {FormatFloat(value[1])}, {FormatFloat(value[2])}, {FormatFloat(value[3])})";
            case EMaterialParameterType.StaticSwitch:
                return $"[Toggle] {identifier} (\"{display}\", Float) = {(value[0] >= 0.5f ? 1 : 0)}";
            default:
                return null;
        }
    }

    private static string? TryBuildTexture(string rawName, int bucket, HashSet<string> emittedIds)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        string identifier = ToIdentifier(rawName);
        if (!emittedIds.Add(identifier)) return null;

        // Keyed by what the engine CALLS the kind, not by which bucket it happens to be: the
        // buckets move between engine versions and the kinds do not. This mapping is the only
        // thing here that is Unity's vocabulary rather than Unreal's, which is why it lives on
        // this side at all.
        string kind = MaterialTextureOrder.KindOf(bucket);
        string shaderlabType = kind switch
        {
            "Standard2D" or "Virtual" => "2D",
            "Cube" => "Cube",
            "Array2D" => "2DArray",
            "ArrayCube" => "CubeArray",
            "Volume" or "SparseVolume" => "3D",
            _ => "2D",
        };
        string defaultLiteral = shaderlabType == "2D" ? "\"white\" {}" : "\"\" {}";
        string display = EscapeDisplayName(rawName);
        return $"{identifier} (\"{display}\", {shaderlabType}) = {defaultLiteral}";
    }

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
