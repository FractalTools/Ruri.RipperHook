using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal sealed record MaterialSymbolSource(
    string MaterialPath,
    SerializedProgramData Metadata,
    int Score,
    bool UsedLoadedMaterialResources,
    MaterialUniformBufferLayout? MaterialLayout);

internal sealed class MaterialJsonSymbolReader
{
    private readonly string _exportRoot;
    private readonly string _exportRootName;
    private readonly Dictionary<string, MaterialSymbolSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MaterialJsonSymbolReader(string exportRoot)
    {
        _exportRoot = exportRoot;
        _exportRootName = Path.GetFileName(exportRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public MaterialSymbolSource? GetSource(string materialPath, string? shaderPlatform = null)
    {
        string normalizedPath = materialPath.Replace('\\', '/');
        string cacheKey = string.IsNullOrWhiteSpace(shaderPlatform)
            ? normalizedPath
            : normalizedPath + "|" + shaderPlatform;
        if (_cache.TryGetValue(cacheKey, out MaterialSymbolSource? cached))
        {
            return cached;
        }

        string? jsonPath = ResolveMaterialJsonPath(normalizedPath);
        if (jsonPath == null || !File.Exists(jsonPath))
        {
            _cache[cacheKey] = null;
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            _cache[cacheKey] = null;
            return null;
        }

        SymbolInputs? inputs = SymbolInputsReader.Read(normalizedPath, shaderPlatform, root[0]);
        if (inputs == null)
        {
            _cache[cacheKey] = null;
            return null;
        }

        // Resolve `MaterialCollection<i>` cbuffers from the material's
        // referenced UMaterialParameterCollection assets — these aren't in the
        // Material UB itself, they're separate bindings that previously
        // collapsed to anonymous `_m0[N]` flat arrays.
        MaterialParameterCollectionReader.ResolveAndInject(root[0], inputs, _exportRoot, _exportRootName);

        MaterialSymbolSource source = BuildSource(normalizedPath, inputs);
        _cache[cacheKey] = source;
        return source;
    }

    private string? ResolveMaterialJsonPath(string materialPath)
    {
        string normalized = materialPath.TrimStart('/');
        if (!string.IsNullOrEmpty(_exportRootName) &&
            normalized.StartsWith(_exportRootName + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[(_exportRootName.Length + 1)..];
        }

        string relative = normalized.Replace('/', Path.DirectorySeparatorChar);
        string direct = Path.Combine(_exportRoot, relative + ".json");
        if (File.Exists(direct))
        {
            return direct;
        }

        int dotIndex = relative.LastIndexOf('.');
        if (dotIndex > 0)
        {
            string withoutObjectSuffix = relative[..dotIndex];
            string alias = Path.Combine(_exportRoot, withoutObjectSuffix + ".json");
            if (File.Exists(alias))
            {
                return alias;
            }
        }

        return null;
    }

    private static MaterialSymbolSource BuildSource(string materialPath, SymbolInputs inputs)
    {
        return new MaterialSymbolSource(
            materialPath,
            MaterialSymbolMetadataBuilder.Build(inputs),
            inputs.UsedLoadedMaterialResources ? 2 : inputs.NumericParameterInfos.Count > 0 ? 1 : 0,
            inputs.UsedLoadedMaterialResources,
            inputs.MaterialResourceCounts != null ? new MaterialUniformBufferLayout(inputs.MaterialResourceCounts) : null);
    }
}

internal sealed class UnifiedMaterialReader
{
    private readonly Dictionary<string, JsonElement>? _materialInterfaces;
    private readonly JsonDocument? _document;
    private readonly Dictionary<string, MaterialSymbolSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private UnifiedMaterialReader(JsonDocument document, Dictionary<string, JsonElement> materialInterfaces)
    {
        _document = document;
        _materialInterfaces = materialInterfaces;
    }

    public static UnifiedMaterialReader? LoadFromFile(string unifiedMetadataPath)
    {
        if (string.IsNullOrWhiteSpace(unifiedMetadataPath) || !File.Exists(unifiedMetadataPath))
        {
            return null;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(File.ReadAllText(unifiedMetadataPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("MaterialInterfaces", out JsonElement mi) || mi.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return null;
            }

            Dictionary<string, JsonElement> materialInterfaces = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty prop in mi.EnumerateObject())
            {
                materialInterfaces[NormalizeKey(prop.Name)] = prop.Value;
            }

            return new UnifiedMaterialReader(document, materialInterfaces);
        }
        catch
        {
            return null;
        }
    }

    public JsonElement? TryGetUniformExpressionSet(string materialPath, string? shaderPlatform = null)
    {
        if (_materialInterfaces == null)
        {
            return null;
        }

        string normalizedPath = materialPath.Replace('\\', '/');
        if (!TryResolveMaterialEntry(normalizedPath, out JsonElement materialEntry))
        {
            return null;
        }

        return SelectUniformExpressionSet(materialEntry, shaderPlatform);
    }

    // Returns the JsonElement for the material's `RenderState` field if it
    // was populated by Pass020. Null when the asset wasn't a UMaterialInterface
    // subclass that carries render state (functions, collections), or when
    // the unified metadata file pre-dates the render-state writer.
    public JsonElement? TryGetRenderState(string materialPath)
    {
        if (_materialInterfaces == null)
        {
            return null;
        }

        string normalizedPath = materialPath.Replace('\\', '/');
        if (!TryResolveMaterialEntry(normalizedPath, out JsonElement materialEntry))
        {
            return null;
        }

        if (!materialEntry.TryGetProperty("RenderState", out JsonElement renderState) || renderState.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return renderState.Clone();
    }

    public MaterialSymbolSource? GetSource(string materialPath, string? shaderPlatform = null)
    {
        if (_materialInterfaces == null)
        {
            return null;
        }

        string normalizedPath = materialPath.Replace('\\', '/');
        string cacheKey = string.IsNullOrWhiteSpace(shaderPlatform)
            ? normalizedPath
            : normalizedPath + "|" + shaderPlatform;
        if (_cache.TryGetValue(cacheKey, out MaterialSymbolSource? cached))
        {
            return cached;
        }

        if (!TryResolveMaterialEntry(normalizedPath, out JsonElement materialEntry))
        {
            _cache[cacheKey] = null;
            return null;
        }

        // Path 1 — UniformExpressionSet from the inline shader map (older /
        // non-IoStore cooks). When present, this is the gold standard
        // because it carries name + byte-offset + type for every CB
        // member in `Material_m0[N]`.
        JsonElement? uniformExpressionSet = SelectUniformExpressionSet(materialEntry, shaderPlatform);
        if (uniformExpressionSet.HasValue)
        {
            SymbolInputs? inputs = SymbolInputsReader.ReadFromUniformExpressionSet(normalizedPath, shaderPlatform, uniformExpressionSet.Value);
            if (inputs != null)
            {
                MaterialSymbolSource source = new(
                    normalizedPath,
                    MaterialSymbolMetadataBuilder.Build(inputs),
                    inputs.UsedLoadedMaterialResources ? 2 : inputs.NumericParameterInfos.Count > 0 ? 1 : 0,
                    inputs.UsedLoadedMaterialResources,
                    inputs.MaterialResourceCounts != null ? new MaterialUniformBufferLayout(inputs.MaterialResourceCounts) : null);
                _cache[cacheKey] = source;
                return source;
            }
        }

        // Path 2 — CachedParameters (parameter NAMES only). Used when the
        // inline shader map is gone (modern UE5 IoStore cook). We can't
        // reconstruct byte offsets from cached data alone, so the
        // resulting source has parameter names but no constant-buffer
        // layout — downstream patcher uses the names for OpName patches
        // and falls through to anonymous Material_Tn for unnamed CB
        // members. The author-facing names (vs `Material_m0`) are still
        // a 100% improvement over the no-symbol baseline.
        if (materialEntry.TryGetProperty("CachedParameters", out JsonElement cached2)
            && cached2.ValueKind == JsonValueKind.Object)
        {
            MaterialSymbolSource? cachedSource = BuildSourceFromCachedParameters(normalizedPath, cached2);
            _cache[cacheKey] = cachedSource;
            return cachedSource;
        }

        _cache[cacheKey] = null;
        return null;
    }

    private static MaterialSymbolSource? BuildSourceFromCachedParameters(string materialPath, JsonElement cachedParams)
    {
        var metadata = new SerializedProgramData
        {
            DebugName = materialPath,
        };

        // Best-effort: collect every name from the typed buckets the
        // CachedParameterNames DTO writes. Bucket-name collisions are
        // tolerated — duplicates land in the same flat name list.
        List<string> textureNames = new();
        AppendStringArray(cachedParams, "TextureNames", textureNames);
        AppendStringArray(cachedParams, "RuntimeVirtualTextureNames", textureNames);
        AppendStringArray(cachedParams, "SparseVolumeTextureNames", textureNames);
        AppendStringArray(cachedParams, "FontNames", textureNames);

        // Texture parameter names go directly into the metadata's
        // TextureParameters slot — the patcher matches by texture
        // bind index, not by name, so the order here doesn't matter
        // structurally. Each name takes a synthetic bind index.
        for (int i = 0; i < textureNames.Count; i++)
        {
            metadata.TextureParameters.Add(new TextureParameter
            {
                Name = textureNames[i],
                NameIndex = -1,
                Index = i,
                SamplerIndex = -1,
                MultiSampled = false,
                Dim = 2,
            });
        }

        // Numeric parameter names go onto a synthetic `Material` cbuffer
        // entry. Without byte offsets we can't pin them to specific
        // packoffset c-N slots, so we just expose the name list — the
        // patcher will leave individual members anonymous but spirv-cross
        // will still emit the cbuffer name as `Material` (vs `type_Material`
        // with no friendly name). The list is preserved so a future
        // resolution pass (e.g. preshader replay) can reorder them.
        var materialCb = new ConstantBufferParameter
        {
            Name = "Material",
            Size = 0,
        };
        List<VectorParameter> vectorParams = new();
        int slot = 0;
        AppendNumericVectorParams(cachedParams, "ScalarNames", ref slot, vectorParams);
        AppendNumericVectorParams(cachedParams, "VectorNames", ref slot, vectorParams);
        AppendNumericVectorParams(cachedParams, "StaticSwitchNames", ref slot, vectorParams);
        AppendNumericVectorParams(cachedParams, "UnknownKindNames", ref slot, vectorParams);
        if (vectorParams.Count > 0)
        {
            materialCb.VectorParameters = vectorParams.ToArray();
            metadata.ConstantBufferParameters.Add(materialCb);
        }

        if (metadata.TextureParameters.Count == 0 && metadata.ConstantBufferParameters.Count == 0)
        {
            return null;
        }

        // Score = 1 — non-zero so the source is preferred over a null
        // result, but lower than score = 2 reserved for the inline-shader-
        // map path (which has byte-offset accuracy).
        return new MaterialSymbolSource(materialPath, metadata, Score: 1, UsedLoadedMaterialResources: false, MaterialLayout: null);
    }

    private static void AppendStringArray(JsonElement owner, string property, List<string> dest)
    {
        if (!owner.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement v in arr.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.String)
            {
                string? s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) dest.Add(s!);
            }
        }
    }

    private static void AppendNumericVectorParams(JsonElement owner, string property, ref int slot, List<VectorParameter> dest)
    {
        if (!owner.TryGetProperty(property, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String) continue;
            string? name = v.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            dest.Add(new VectorParameter
            {
                Name = name!,
                NameIndex = -1,
                Type = ShaderParamType.Float,
                Index = slot * 16,        // synthetic byte offset; byte-offset pinning relies on the inline-shader-map path
                ArraySize = 1,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
            slot++;
        }
    }

    private bool TryResolveMaterialEntry(string materialPath, out JsonElement entry)
    {
        entry = default;
        if (_materialInterfaces == null)
        {
            return false;
        }

        foreach (string candidate in EnumerateLookupKeys(materialPath))
        {
            if (_materialInterfaces.TryGetValue(NormalizeKey(candidate), out entry))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateLookupKeys(string materialPath)
    {
        string normalized = materialPath.Replace('\\', '/').Trim();
        if (normalized.Length == 0)
        {
            yield break;
        }

        yield return normalized;

        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            yield return normalized.TrimStart('/');
        }
        else
        {
            yield return "/" + normalized;
        }

        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        if (dotIndex > slashIndex)
        {
            yield return normalized[..dotIndex];
        }

        int contentMarker = normalized.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentMarker >= 0)
        {
            string after = normalized[(contentMarker + "/Content/".Length)..];
            yield return after;
            yield return "/" + after;
        }
    }

    private static string NormalizeKey(string key) => key.Replace('\\', '/').Trim().TrimStart('/');

    private static JsonElement? SelectUniformExpressionSet(JsonElement materialEntry, string? preferredShaderPlatform)
    {
        if (!materialEntry.TryGetProperty("LoadedShaderMaps", out JsonElement loadedShaderMaps) || loadedShaderMaps.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? fallback = null;
        foreach (JsonElement shaderMap in loadedShaderMaps.EnumerateArray())
        {
            if (shaderMap.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!shaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement content) || content.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!content.TryGetProperty("UniformExpressionSet", out JsonElement ues) || ues.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? shaderPlatform = ReadString(shaderMap, "ShaderPlatform");
            if (!string.IsNullOrWhiteSpace(preferredShaderPlatform) && string.Equals(shaderPlatform, preferredShaderPlatform, StringComparison.OrdinalIgnoreCase))
            {
                return ues.Clone();
            }

            fallback ??= ues.Clone();
        }

        return fallback;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
