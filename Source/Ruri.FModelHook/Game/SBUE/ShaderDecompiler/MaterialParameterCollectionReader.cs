using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Resolves the project's `UMaterialParameterCollection` (MPC) assets that a
// material references, and produces a `MaterialCollection<i>` cbuffer layout
// per UE's `UMaterialParameterCollection::CreateBufferStruct`
// (`Engine/Source/Runtime/Engine/Private/Materials/ParameterCollection.cpp:531-556`).
//
// UE layout:
//   const uint32 NumVectors = DivideAndRoundUp(ScalarParameters.Num(), 4)
//                             + VectorParameters.Num();
//   ParameterData = { float4 Vectors[NumVectors] };
//
// Slot order within the array, matching `UMaterialParameterCollection::GetDefaultParameterData`:
//   slot[0..ceil(N_scalars/4)-1] = packed scalars, .xyzw in declaration order
//   slot[ceil(N_scalars/4)..]    = vector parameters, one per slot
//
// The cooked shader sometimes binds a TRUNCATED suffix of this array (DXC drops
// unused tail registers during HLSL→SPIR-V), but the named-member offsets are
// still authoritative — Pass050's tail-fill will synthesize a `_TODO_missing_seed_field`
// if needed, never breaking a recovery.
internal static class MaterialParameterCollectionReader
{
    private static readonly Dictionary<string, ConstantBufferParameter?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    // Resolves all `MaterialCollection<i>` cbuffers that `asset` references via
    // its `CachedExpressionData.ParameterCollectionInfos[]`. Appends them to
    // `inputs.ExtraConstantBuffers`. `exportRoot` is the project export folder
    // (e.g. `<Output>/Exports/Oni_Valley_VFX`); `exportRootName` is its leaf
    // name (`Oni_Valley_VFX`), used to strip the `/Game/` prefix from UE
    // object paths.
    public static void ResolveAndInject(JsonElement asset, SymbolInputs inputs, string exportRoot, string exportRootName)
    {
        if (!asset.TryGetProperty("CachedExpressionData", out JsonElement ced)
            || ced.ValueKind != JsonValueKind.Object
            || !ced.TryGetProperty("ParameterCollectionInfos", out JsonElement pcis)
            || pcis.ValueKind != JsonValueKind.Array)
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

        // Match UE's GetDefaultParameterData ordering exactly:
        //   for each scalar i: pack into vec4 floor(i/4), component i%4
        //   for each vector j: emit vec4 at slot (ceil(NScalars/4) + j)
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

    // Maps a UE object path like `/Game/Oni_Project/.../Level_material_parameters.0`
    // to the on-disk JSON path. `/Game/` corresponds to `<exportRoot>/Content/`
    // (FModel's standard export convention; the export root's leaf name is the
    // project name, and Content sits directly under it).
    private static string? ResolveAssetPath(string objectPath, string exportRoot, string exportRootName)
    {
        // Trim trailing `.<n>` UObject sub-object suffix and leading `/`
        string trimmed = objectPath.TrimStart('/');
        int dotIdx = trimmed.LastIndexOf('.');
        if (dotIdx > 0)
        {
            trimmed = trimmed[..dotIdx];
        }

        // `/Game/Foo/Bar` → `<exportRoot>/Content/Foo/Bar.json`
        if (trimmed.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            string rel = "Content/" + trimmed["Game/".Length..];
            string p = Path.Combine(exportRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".json");
            if (File.Exists(p)) return p;
        }
        // Engine assets: `/Engine/...` → `<sibling-of-exportRoot>/Engine/...`
        // Most projects don't ship MPCs under /Engine, so this is rare.
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
        // Collapse multiple underscores
        string result = sb.ToString();
        while (result.Contains("__")) result = result.Replace("__", "_");
        return result.Trim('_').Length == 0 ? "_" : result;
    }
}
