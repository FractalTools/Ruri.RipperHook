using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 040 — Build the shaderlab `Properties { ... }` block for every
// shader-map and stash the result in `map.PropertiesBlock`.
//
// Source of truth: each material's FUniformExpressionSet — the same
// `UnifiedShaderMetadata.json[MaterialInterfaces.<x>].LoadedShaderMaps[*]
// .MaterialShaderMapContent.UniformExpressionSet` tree the symbol path
// already reads. Pass030 (LoadSymbolSources) wires the reader; this
// pass calls it once per shader-map and renders into a string.
//
// UE -> shaderlab mapping (UE 5.2 source):
//
//   UniformNumericParameters[i].ParameterType
//     Engine/Source/Runtime/Engine/Public/MaterialTypes.h:188-206
//     `enum class EMaterialParameterType`
//       - Scalar       -> `Float`
//       - Vector       -> `Color`  (FLinearColor on the wire)
//       - DoubleVector -> `Vector` (no 0..1 inspector clamp)
//       - StaticSwitch -> `[Toggle] _X ("name", Float) = 0|1`
//
//   UniformTextureParameters[Type][i] outer-array index aligns with
//     Engine/Source/Runtime/Engine/Public/MaterialShared.h:464-475
//     `enum class EMaterialTextureParameterType`
//       - 0 Standard2D -> `2D`        default `"white" {}`
//       - 1 Cube       -> `Cube`      default `"" {}`
//       - 2 Array2D    -> `2DArray`   default `"" {}`
//       - 3 ArrayCube  -> `CubeArray` default `"" {}`
//       - 4 Volume     -> `3D`        default `"" {}`
//       - 5 Virtual    -> `2D`        default `"white" {}` (no first-class
//                                                          shaderlab VT)
//
// The cooked Material UB packs these slots in the same order
// `MaterialUniformBufferLayout` already replays
//   Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-503
// so the Properties block is positionally aligned with the cbuffer
// member layout downstream HLSL emission produces.
internal static class Pass170_BuildShaderLabProperties
{
    public static void DoPass(PipelineState state)
    {
        if (state.UnifiedMaterialReader == null)
        {
            // Sidecar-only run (no UnifiedShaderMetadata.json) — leaves
            // every map.PropertiesBlock at its default empty string.
            // Downstream emit just skips the block.
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
        }

        state.Log($"    Properties: populated {populated}/{state.ShaderMaps.Count} shader-maps.");
    }

    private static string BuildBlockForMap(PipelineState state, ShaderMapInfo map)
    {
        // Walk every asset attached to the shader-map. First non-empty
        // Properties block wins — material instances sharing a parent
        // produce the same cooked UES member layout, so picking any of
        // them yields the same Property surface.
        foreach (string asset in map.Assets)
        {
            JsonElement? ues = state.UnifiedMaterialReader!.TryGetUniformExpressionSet(asset);
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
        // UE engine selection-highlight uniform; runtime-overridden so it
        // would never appear in any material editor UI. Skip from the
        // shaderlab Properties surface.
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
            5 => "2D", // EMaterialTextureParameterType::Virtual binds a 2D sampler.
            _ => "2D",
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
            // Older UE serialisations packed scalar into FLinearColor.R.
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

    // shaderlab Property identifier rules: [A-Za-z0-9_], starts with `_`.
    // UE parameter names can contain spaces, hyphens, dots, etc.; replace
    // anything illegal with `_` and ensure a leading underscore.
    private static string ToIdentifier(string raw)
    {
        StringBuilder sb = new(raw.Length + 1);
        sb.Append('_');
        foreach (char c in raw)
        {
            sb.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_');
        }
        // Collapse runs of underscores for readability.
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
        // Round-trip format so 0.7 doesn't emit as 0.69999..., trim
        // trailing zeroes / decimal point for whole numbers.
        string s = value.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('.') && !s.Contains('e') && !s.Contains('E'))
        {
            s = s.TrimEnd('0').TrimEnd('.');
            if (s.Length == 0 || s == "-") s = "0";
        }
        return s;
    }
}
