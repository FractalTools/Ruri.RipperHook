using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class MaterialTextureOrder
{
    public static List<string> Extract(JsonElement uniformExpressionSet)
    {
        return Extract(uniformExpressionSet, out _);
    }

    public static List<string> Extract(JsonElement uniformExpressionSet, out List<int> bucketIndices)
    {
        var names = new List<string>();
        bucketIndices = new List<int>();
        if (uniformExpressionSet.ValueKind != JsonValueKind.Object) return names;
        if (!uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement buckets)
            || buckets.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        int bucketIndex = -1;
        foreach (JsonElement bucket in buckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Array) continue;
            bucketIndex++;
            foreach (JsonElement entry in bucket.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                names.Add(ReadName(entry) ?? $"Texture_{names.Count}");
                bucketIndices.Add(bucketIndex);
            }
        }
        return names;
    }

    private static string? ReadName(JsonElement entry)
    {
        if (entry.TryGetProperty("ParameterName", out JsonElement direct)
            && direct.ValueKind == JsonValueKind.String)
        {
            string? value = direct.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value != "None") return value;
        }
        if (entry.TryGetProperty("ParameterInfo", out JsonElement info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("Name", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            string? value = nested.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value != "None") return value;
        }
        return null;
    }
}
