using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class Pass170_BuildShaderLabProperties
{
    public static void DoPass(PipelineState state)
    {
        if (state.UnifiedMaterialReader == null)
        {
            state.Log("    Properties: skipped (no UnifiedMaterialReader).");
            return;
        }

        int populated = 0;
        foreach (ShaderMapInfo map in state.ShaderMaps)
        {
            string block = BuildBlockForMap(state, map);
            if (!string.IsNullOrEmpty(block))
            {
                map.PropertiesBlock = block;
                populated++;
            }

            foreach (string asset in map.Assets)
            {
                System.Text.Json.JsonElement? ues = state.UnifiedMaterialReader!.TryGetUniformExpressionSet(asset, shaderMapHash: map.ShaderMapHash);
                if (ues == null) continue;
                map.MaterialTextureOrder = new List<string>(MaterialTextureOrder.Extract(ues.Value, out List<int> textureBuckets));
                map.MaterialTextureBuckets = textureBuckets;

                MaterialConstantBufferReader.Read(ues.Value, asset);
                if (MaterialConstantBufferReader.EvaluatedCbufferValues.TryGetValue(asset, out var vals))
                {
                    map.MaterialCbufferValues = new Dictionary<string, string>(vals, StringComparer.Ordinal);
                }
                if (Environment.GetEnvironmentVariable("RURI_PRESHADER_DEBUG") == "1")
                {
                    foreach ((string plat, int size, int count) in state.UnifiedMaterialReader!.EnumerateUniformExpressionSets(asset))
                    {
                        state.Log($"    [ues-candidate] {asset} platform={plat} PreshaderBufferSize={size} NumPreshaders={count}");
                    }
                }
                if (MaterialConstantBufferReader.EvaluatedCbufferOffsets.TryGetValue(asset, out var offs))
                {
                    map.MaterialCbufferOffsets = new Dictionary<string, int>(offs, StringComparer.Ordinal);
                }
                if (MaterialConstantBufferReader.EvaluatedCbufferPrograms.TryGetValue(asset, out var progs))
                {
                    map.MaterialCbufferPrograms = new Dictionary<string, string>(progs, StringComparer.Ordinal);
                }
                if (MaterialConstantBufferReader.EvaluatedCbufferParams.TryGetValue(asset, out var pars))
                {
                    map.MaterialCbufferParams = new Dictionary<string, string>(pars, StringComparer.Ordinal);
                }

                if (map.MaterialTextureOrder.Count > 0) break;
            }
        }

        state.Log($"    Properties: populated {populated}/{state.ShaderMaps.Count} shader-maps.");
        state.Log($"    UES 选取:哈希精确命中 {UnifiedMaterialReader.HashMatchedSelections} 次,"
                  + $"退回启发式 {UnifiedMaterialReader.HeuristicSelections} 次(退回的都是在赌 cbuffer 布局)。");
    }

    private static string BuildBlockForMap(PipelineState state, ShaderMapInfo map)
    {
        foreach (string asset in map.Assets)
        {
            JsonElement? ues = state.UnifiedMaterialReader!.TryGetUniformExpressionSet(asset, shaderMapHash: map.ShaderMapHash);
            if (!ues.HasValue) continue;

            var lines = new List<string>();
            HashSet<string> emittedIds = new(StringComparer.Ordinal);

            if (ues.Value.TryGetProperty("UniformNumericParameters", out JsonElement numerics)
                && numerics.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement param in numerics.EnumerateArray())
                {
                    string? line = TryBuildNumeric(param, emittedIds);
                    if (line != null) lines.Add(line);
                }
            }

            if (ues.Value.TryGetProperty("UniformTextureParameters", out JsonElement textureBuckets)
                && textureBuckets.ValueKind == JsonValueKind.Array)
            {
                int typeIndex = 0;
                foreach (JsonElement bucket in textureBuckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Array) { typeIndex++; continue; }
                    foreach (JsonElement texParam in bucket.EnumerateArray())
                    {
                        string? line = TryBuildTexture(texParam, typeIndex, emittedIds);
                        if (line != null) lines.Add(line);
                    }
                    typeIndex++;
                }
            }

            if (lines.Count == 0) continue;

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
        return string.Empty;
    }

    private static string? TryBuildNumeric(JsonElement param, HashSet<string> emittedIds)
    {
        string rawName = param.TryGetProperty("ParameterName", out JsonElement nameElem)
            ? nameElem.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(rawName) || string.Equals(rawName, "None", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(rawName, "SelectionColor", StringComparison.OrdinalIgnoreCase)) return null;

        string identifier = ToIdentifier(rawName);
        if (!emittedIds.Add(identifier)) return null;

        string parameterType = param.TryGetProperty("ParameterType", out JsonElement typeElem)
            ? typeElem.GetString() ?? string.Empty
            : string.Empty;
        string display = EscapeDisplayName(rawName);

        switch (parameterType)
        {
            case "Scalar":
                return $"{identifier} (\"{display}\", Float) = {FormatFloat(ReadScalar(param))}";
            case "Vector":
                {
                    (double r, double g, double b, double a) = ReadVector(param);
                    return $"{identifier} (\"{display}\", Color) = ({FormatFloat(r)}, {FormatFloat(g)}, {FormatFloat(b)}, {FormatFloat(a)})";
                }
            case "DoubleVector":
                {
                    (double r, double g, double b, double a) = ReadVector(param);
                    return $"{identifier} (\"{display}\", Vector) = ({FormatFloat(r)}, {FormatFloat(g)}, {FormatFloat(b)}, {FormatFloat(a)})";
                }
            case "StaticSwitch":
                return $"[Toggle] {identifier} (\"{display}\", Float) = {(ReadScalar(param) >= 0.5 ? 1 : 0)}";
            default:
                return null;
        }
    }

    private static string? TryBuildTexture(JsonElement texParam, int typeIndex, HashSet<string> emittedIds)
    {
        string rawName = texParam.TryGetProperty("ParameterName", out JsonElement nameElem)
            ? nameElem.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(rawName) || string.Equals(rawName, "None", StringComparison.OrdinalIgnoreCase)) return null;

        string identifier = ToIdentifier(rawName);
        if (!emittedIds.Add(identifier)) return null;

        string shaderlabType = typeIndex switch
        {
            0 => "2D",
            1 => "Cube",
            2 => "2DArray",
            3 => "CubeArray",
            4 => "3D",
            5 => "2D",            _ => "2D",
        };
        string defaultLiteral = typeIndex switch
        {
            0 or 5 => "\"white\" {}",
            _ => "\"\" {}",
        };
        string display = EscapeDisplayName(rawName);
        return $"{identifier} (\"{display}\", {shaderlabType}) = {defaultLiteral}";
    }

    private static double ReadScalar(JsonElement param)
    {
        if (!param.TryGetProperty("Value", out JsonElement value)) return 0.0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Object => value.TryGetProperty("R", out JsonElement r) ? r.GetDouble() : 0.0,
            _ => 0.0,
        };
    }

    private static (double R, double G, double B, double A) ReadVector(JsonElement param)
    {
        if (!param.TryGetProperty("Value", out JsonElement value)) return (0, 0, 0, 0);
        if (value.ValueKind == JsonValueKind.Object)
        {
            double r = value.TryGetProperty("R", out JsonElement vr) ? vr.GetDouble() : 0.0;
            double g = value.TryGetProperty("G", out JsonElement vg) ? vg.GetDouble() : 0.0;
            double b = value.TryGetProperty("B", out JsonElement vb) ? vb.GetDouble() : 0.0;
            double a = value.TryGetProperty("A", out JsonElement va) ? va.GetDouble() : 0.0;
            return (r, g, b, a);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            double[] xs = new double[4];
            int i = 0;
            foreach (JsonElement el in value.EnumerateArray())
            {
                if (i >= 4) break;
                if (el.ValueKind == JsonValueKind.Number) xs[i] = el.GetDouble();
                i++;
            }
            return (xs[0], xs[1], xs[2], xs[3]);
        }
        return (0, 0, 0, 0);
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
