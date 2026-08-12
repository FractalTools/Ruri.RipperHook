using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class MaterialParameterCollectionReader
{
    private static readonly Dictionary<string, ConstantBufferParameter?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    public static void ResolveAndInject(JsonElement asset, SymbolInputs inputs, string exportRoot, string exportRootName)
    {
        JsonElement pcis = FindParameterCollectionInfos(asset, exportRoot, exportRootName, maxHops: 8);
        if (pcis.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement pci in pcis.EnumerateArray())
        {
            if (!pci.TryGetProperty("ParameterCollection", out JsonElement pc)
                || pc.ValueKind != JsonValueKind.Object
                || !pc.TryGetProperty("ObjectPath", out JsonElement opEl)
                || opEl.ValueKind != JsonValueKind.String)
            {
                index++;
                continue;
            }

            string objectPath = opEl.GetString() ?? "";
            ConstantBufferParameter? cb = LoadCollection(objectPath, index, exportRoot, exportRootName);
            if (cb != null)
            {
                inputs.ExtraConstantBuffers.Add(cb);
            }
            index++;
        }
    }

    private static JsonElement FindParameterCollectionInfos(JsonElement asset, string exportRoot, string exportRootName, int maxHops)
    {
        JsonElement current = asset;
        for (int hop = 0; hop <= maxHops; hop++)
        {
            if (current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty("CachedExpressionData", out JsonElement ced)
                && ced.ValueKind == JsonValueKind.Object
                && ced.TryGetProperty("ParameterCollectionInfos", out JsonElement pcis)
                && pcis.ValueKind == JsonValueKind.Array
                && pcis.GetArrayLength() > 0)
            {
                return pcis;
            }
            if (!TryResolveParentAsset(current, exportRoot, exportRootName, out current))
            {
                break;
            }
        }
        return default;
    }

    private static bool TryResolveParentAsset(JsonElement asset, string exportRoot, string exportRootName, out JsonElement parent)
    {
        parent = default;
        if (asset.ValueKind != JsonValueKind.Object) return false;
        if (!asset.TryGetProperty("Properties", out JsonElement props)
            || props.ValueKind != JsonValueKind.Object) return false;
        if (!props.TryGetProperty("Parent", out JsonElement parentRef)
            || parentRef.ValueKind != JsonValueKind.Object) return false;
        if (!parentRef.TryGetProperty("ObjectPath", out JsonElement opEl)
            || opEl.ValueKind != JsonValueKind.String) return false;

        string objectPath = opEl.GetString() ?? "";
        string? jsonPath = ResolveAssetPath(objectPath, exportRoot, exportRootName);
        if (jsonPath == null || !File.Exists(jsonPath)) return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array
                || doc.RootElement.GetArrayLength() == 0)
            {
                return false;
            }
            parent = doc.RootElement[0].Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ConstantBufferParameter? LoadCollection(string objectPath, int collectionIndex, string exportRoot, string exportRootName)
    {
        string cbName = $"MaterialCollection{collectionIndex}";
        string cacheKey = cbName + "|" + objectPath;
        if (s_cache.TryGetValue(cacheKey, out ConstantBufferParameter? cached))
        {
            return cached;
        }

        string? jsonPath = ResolveAssetPath(objectPath, exportRoot, exportRootName);
        if (jsonPath == null || !File.Exists(jsonPath))
        {
            s_cache[cacheKey] = null;
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                s_cache[cacheKey] = null;
                return null;
            }

            JsonElement mpc = root[0];
            if (!mpc.TryGetProperty("Properties", out JsonElement props) || props.ValueKind != JsonValueKind.Object)
            {
                s_cache[cacheKey] = null;
                return null;
            }

            ConstantBufferParameter cb = BuildCollectionCb(cbName, props);
            s_cache[cacheKey] = cb;
            return cb;
        }
        catch
        {
            s_cache[cacheKey] = null;
            return null;
        }
    }

    private static ConstantBufferParameter BuildCollectionCb(string cbName, JsonElement props)
    {
        List<JsonElement> scalars = ReadArray(props, "ScalarParameters");
        List<JsonElement> vectors = ReadArray(props, "VectorParameters");

        List<VectorParameter> vectorParams = new();
        for (int i = 0; i < scalars.Count; i++)
        {
            string name = ReadParameterName(scalars[i]) ?? $"Scalar_{i}";
            int slot = i / 4;
            int component = i % 4;
            int byteOffset = slot * 16 + component * 4;
            vectorParams.Add(new VectorParameter
            {
                Name = SanitizeIdent(name),
                NameIndex = -1,
                Type = ShaderParamType.Float,
                Index = byteOffset,
                ArraySize = 1,
                IsMatrix = false,
                RowCount = 1,
                ColumnCount = 1,
            });
        }

        int scalarSlots = (scalars.Count + 3) / 4;
        for (int j = 0; j < vectors.Count; j++)
        {
            string name = ReadParameterName(vectors[j]) ?? $"Vector_{j}";
            int byteOffset = (scalarSlots + j) * 16;
            vectorParams.Add(new VectorParameter
            {
                Name = SanitizeIdent(name),
                NameIndex = -1,
                Type = ShaderParamType.Float,
                Index = byteOffset,
                ArraySize = 1,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
        }

        int totalSlots = scalarSlots + vectors.Count;
        return new ConstantBufferParameter
        {
            Name = cbName,
            NameIndex = -1,
            VectorParameters = vectorParams.ToArray(),
            MatrixParameters = Array.Empty<MatrixParameter>(),
            StructParameters = Array.Empty<StructParameter>(),
            Size = totalSlots * 16,
            IsPartialCB = false,
        };
    }

    private static List<JsonElement> ReadArray(JsonElement props, string key)
    {
        if (props.TryGetProperty(key, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            List<JsonElement> result = new(arr.GetArrayLength());
            foreach (JsonElement e in arr.EnumerateArray()) result.Add(e);
            return result;
        }
        return new List<JsonElement>();
    }

    private static string? ReadParameterName(JsonElement param)
    {
        if (param.ValueKind != JsonValueKind.Object) return null;
        if (param.TryGetProperty("ParameterName", out JsonElement nameEl) && nameEl.ValueKind == JsonValueKind.String)
        {
            return nameEl.GetString();
        }
        return null;
    }

    private static string? ResolveAssetPath(string objectPath, string exportRoot, string exportRootName)
    {
        string trimmed = objectPath.TrimStart('/');
        int dotIdx = trimmed.LastIndexOf('.');
        if (dotIdx > 0)
        {
            trimmed = trimmed[..dotIdx];
        }

        if (trimmed.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            string rel = "Content/" + trimmed["Game/".Length..];
            string p = Path.Combine(exportRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".json");
            if (File.Exists(p)) return p;
        }
        if (trimmed.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(exportRoot);
            if (parent != null)
            {
                string p = Path.Combine(parent, trimmed.Replace('/', Path.DirectorySeparatorChar) + ".json");
                if (File.Exists(p)) return p;
            }
        }

        return null;
    }

    private static string SanitizeIdent(string s)
    {
        if (string.IsNullOrEmpty(s)) return "Unknown";
        System.Text.StringBuilder sb = new(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool valid = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
            sb.Append(valid ? c : '_');
        }
        if (sb.Length > 0 && sb[0] >= '0' && sb[0] <= '9') sb.Insert(0, '_');
        string result = sb.ToString();
        while (result.Contains("__")) result = result.Replace("__", "_");
        return result.Trim('_').Length == 0 ? "_" : result;
    }
}
