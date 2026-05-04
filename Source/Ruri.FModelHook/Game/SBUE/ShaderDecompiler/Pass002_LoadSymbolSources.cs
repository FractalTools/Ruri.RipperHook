using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 002 — Initialise per-material symbol-source readers. Lazy:
// actual JSON parsing happens later, on first per-material lookup, but
// the readers cache results so Pass 003's hot loop only pays the load
// cost once per material.
//
// Two readers are populated when the unified metadata is available:
//   - UnifiedMaterialReader: pulls UniformExpressionSet straight out of
//     `UnifiedShaderMetadata.json[MaterialInterfaces.<x>]`. Primary
//     path — single JSON open per session.
//   - MaterialJsonSymbolReader: per-material `*.uasset.json` from
//     FModel's RawData export. Fallback for installs where the user
//     right-clicked individual materials but never ran the unified
//     export.
//
// File holds the readers themselves + every supporting type they
// need (SymbolInputs, SymbolBuilder, SymbolInputsReader,
// MaterialSymbolSource, MaterialUniformBufferLayout, FMaterialParameterInfo)
// so Pass 003 can consume `state.UnifiedMaterialReader` /
// `state.MaterialJsonSymbolReader` directly.
internal static class Pass002_LoadSymbolSources
{
    public static void DoPass(PipelineState state)
    {
        string? unifiedPath = state.Options.UnifiedMetadataPath;
        if (!string.IsNullOrEmpty(unifiedPath))
        {
            string exportRoot = Path.GetDirectoryName(unifiedPath) ?? string.Empty;
            if (Directory.Exists(exportRoot))
            {
                state.MaterialJsonSymbolReader = new MaterialJsonSymbolReader(exportRoot);
            }
            if (File.Exists(unifiedPath))
            {
                state.UnifiedMaterialReader = UnifiedMaterialReader.LoadFromFile(unifiedPath);
            }
        }

        state.Log($"    Symbol sources: unified={(state.UnifiedMaterialReader != null ? "yes" : "no")}, per-material-json={(state.MaterialJsonSymbolReader != null ? "yes" : "no")}.");
    }
}

// =====================================================================
// Mirror of UE's FMaterialParameterInfo (Engine/Public/MaterialTypes.h).
// =====================================================================
internal enum EMaterialParameterAssociation { LayerParameter = 0, BlendParameter = 1, GlobalParameter = 2 }

internal sealed class FMaterialParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public EMaterialParameterAssociation Association { get; set; } = EMaterialParameterAssociation.GlobalParameter;
    public int Index { get; set; } = -1;

    public FMaterialParameterInfo() { }
    public FMaterialParameterInfo(string name, EMaterialParameterAssociation association = EMaterialParameterAssociation.GlobalParameter, int index = -1)
    { Name = name; Association = association; Index = index; }

    public override string ToString() => $"{Name}[{Association}:{Index}]";
}

// =====================================================================
// MaterialSymbolSource — uniform return shape from both readers.
// =====================================================================
internal sealed record MaterialSymbolSource(
    string MaterialPath,
    ShaderSymbolData Metadata,
    string Header,
    int Score,
    bool UsedLoadedMaterialResources,
    MaterialUniformBufferLayout? MaterialLayout);


// =====================================================================
// MaterialUniformBufferLayout
// =====================================================================
// Replays FUniformExpressionSet::CreateBufferStruct() to enumerate
// the resource members of the `Material` uniform buffer in the exact
// order UE writes them, so we can map an SRT ResourceIndex back to a
// canonical name.
//
// Source: Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp:341-503.
//
// Only resource-typed members (UBMT_TEXTURE / UBMT_SRV / UBMT_SAMPLER)
// land in FRHIUniformBufferLayout.Resources[] -- the SRT ResourceIndex
// indexes into that list. Numeric-typed leading members
// (`VTPackedPageTableUniform`, `VTPackedUniform`, `PreshaderBuffer`)
// occupy bytes at the start of the constant buffer but do not consume
// resource slots, so we skip them here.
//
// Order replayed by CreateBufferStruct (every block is conditional on
// its count > 0; absent counts emit zero entries):
//
//   for each Standard2D[i]    : Texture2D_<i>            (UBMT_TEXTURE)
//                                Texture2D_<i>Sampler     (UBMT_SAMPLER)
//   for each Cube[i]          : TextureCube_<i>          (TEXTURE)
//                                TextureCube_<i>Sampler   (SAMPLER)
//   for each Array2D[i]       : Texture2DArray_<i>       (TEXTURE)
//                                Texture2DArray_<i>Sampler(SAMPLER)
//   for each ArrayCube[i]     : TextureCubeArray_<i>     (TEXTURE)
//                                TextureCubeArray_<i>Sampler(SAMPLER)
//   for each Volume[i]        : VolumeTexture_<i>        (TEXTURE)
//                                VolumeTexture_<i>Sampler (SAMPLER)
//   for each External[i]      : ExternalTexture_<i>      (TEXTURE)
//                                ExternalTexture_<i>Sampler (SAMPLER)  // UE source uses MediaTextureSamplerNames which prints "ExternalTexture_<i>Sampler"
//   for each VTStack[i]       : VirtualTexturePageTable0_<i>           (TEXTURE)
//                                VirtualTexturePageTable1_<i>          (TEXTURE)  // only when Stack.NumLayers > 4
//                                VirtualTexturePageTableIndirection_<i>(TEXTURE)
//   for each Virtual[i]       : VirtualTexturePhysical_<i>             (UBMT_SRV, not TEXTURE -- supports sRGB/non-sRGB aliasing)
//                                VirtualTexturePhysical_<i>Sampler     (SAMPLER)
//   Wrap_WorldGroupSettings                                            (SAMPLER, unconditional)
//   Clamp_WorldGroupSettings                                           (SAMPLER, unconditional)
internal sealed class MaterialUniformBufferLayout
{
    private readonly List<string> _resourceMemberNames;

    public MaterialUniformBufferLayout(MaterialResourceCounts counts)
    {
        _resourceMemberNames = BuildResourceMemberNames(counts);
        _typedSlotByAuthorName = BuildAuthorIndex(counts);
    }

    private readonly Dictionary<string, string> _typedSlotByAuthorName;

    // Author-facing parameter name -> typed slot name (e.g. "Bamboo_base_maps"
    // -> "Texture2D_1"). Used by the texture-from-sampler-pair inferrer to
    // resolve sampler names like "Material_Bamboo_base_mapsSampler" back to
    // their typed slot when picking the texture binding name.
    public bool TryResolveAuthorName(string authorName, out string typedSlot)
        => _typedSlotByAuthorName.TryGetValue(authorName, out typedSlot!);

    public string? ResolveResourceName(SrtRecord record)
    {
        int idx = record.ResourceIndex;
        if (idx < 0 || idx >= _resourceMemberNames.Count)
        {
            return null;
        }

        return $"Material_{_resourceMemberNames[idx]}";
    }

    public IReadOnlyList<string> ResourceMemberNames => _resourceMemberNames;

    private static List<string> BuildResourceMemberNames(MaterialResourceCounts counts)
    {
        List<string> result = new();
        AppendTextureSamplerPairs(result, "Texture2D", counts.Standard2D, counts.Standard2DAuthorNames);
        AppendTextureSamplerPairs(result, "TextureCube", counts.Cube, counts.CubeAuthorNames);
        AppendTextureSamplerPairs(result, "Texture2DArray", counts.Array2D, counts.Array2DAuthorNames);
        AppendTextureSamplerPairs(result, "TextureCubeArray", counts.ArrayCube, counts.ArrayCubeAuthorNames);
        AppendTextureSamplerPairs(result, "VolumeTexture", counts.Volume, counts.VolumeAuthorNames);
        AppendTextureSamplerPairs(result, "ExternalTexture", counts.External, counts.ExternalAuthorNames);

        // VirtualTextureStack page tables are inserted between External textures
        // and Virtual physical textures. Each stack emits:
        //   PageTable0_<i>           (TEXTURE)
        //   [PageTable1_<i>          (TEXTURE) — only when Stack.NumLayers > 4]
        //   PageTableIndirection_<i> (TEXTURE)
        //
        // Per-stack `NumLayers` is the source of truth, but it is NOT carried
        // by `UnifiedShaderMetadata.json` (FModel's hook flattens UES without
        // the VTStacks array). When `VirtualTextureStackLayerCounts` is null,
        // we INFER the stack count from the `Resources[]` length: there's a
        // known number of texture entries between the External block and the
        // Virtual physical block, and any TEXTURE entry there must be a VT
        // page-table member. We assume `NumLayers <= 4` for every stack
        // (the dominant case in shipped projects); a `>4`-layer stack would
        // require the actual VTStacks array to disambiguate.
        if (counts.VirtualTextureStackLayerCounts != null)
        {
            AppendVirtualTextureStacks(result, counts.VirtualTextureStackLayerCounts);
        }
        else if (counts.TotalResourceCount is int total)
        {
            int textureSamplerPairsConsumed = 2 * (counts.Standard2D + counts.Cube + counts.Array2D + counts.ArrayCube + counts.Volume + counts.External);
            int virtualPhysicalConsumed = 2 * counts.Virtual;
            int fixedTrailingSamplers = 2; // Wrap + Clamp
            int vtStackTextureCount = total - textureSamplerPairsConsumed - virtualPhysicalConsumed - fixedTrailingSamplers;
            if (vtStackTextureCount > 0 && vtStackTextureCount % 2 == 0)
            {
                int inferredStackCount = vtStackTextureCount / 2;
                List<int> assumedLayers = new(inferredStackCount);
                for (int i = 0; i < inferredStackCount; i++)
                {
                    assumedLayers.Add(2); // <= 4 -> emit PageTable0 + Indirection only
                }
                AppendVirtualTextureStacks(result, assumedLayers);
            }
            // If vtStackTextureCount % 2 != 0, at least one stack must have
            // NumLayers > 4 (3 entries) and we cannot uniquely solve the mix
            // without the actual VTStacks array. Skip the page-table block;
            // downstream layout will be off after this block, so do not name
            // anything past External when this happens. Caller can detect
            // this by comparing ResourceMemberNames.Count vs Resources.Num().
        }

        AppendTextureSamplerPairs(result, "VirtualTexturePhysical", counts.Virtual, counts.VirtualAuthorNames);
        // Fixed members emitted unconditionally by CreateBufferStruct at the end.
        result.Add("Wrap_WorldGroupSettings");
        result.Add("Clamp_WorldGroupSettings");
        return result;
    }

    private static void AppendTextureSamplerPairs(List<string> result, string baseName, int count, IReadOnlyList<string?>? authorNames = null)
    {
        for (int i = 0; i < count; i++)
        {
            string? author = (authorNames != null && i < authorNames.Count) ? authorNames[i] : null;
            // Prefer the author-facing parameter name when present (sanitized
            // for HLSL identifiers); fall back to the typed slot name UE
            // generated via CreateBufferStruct's printf. Either form is
            // source-truth: typed comes from UE's `Texture2D_<i>` template,
            // author-name comes from the `.uasset` ParameterInfo.Name.
            string sanitized = SanitizeHlslIdent(author);
            string textureName = string.IsNullOrEmpty(sanitized) ? $"{baseName}_{i}" : sanitized;
            result.Add(textureName);
            result.Add($"{textureName}Sampler");
        }
    }

    private static string SanitizeHlslIdent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[raw.Length];
        int written = 0;
        foreach (char c in raw)
        {
            char ch = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_';
            buffer[written++] = ch;
        }
        if (written == 0)
        {
            return string.Empty;
        }
        // HLSL identifier cannot start with a digit; prepend underscore.
        if (buffer[0] >= '0' && buffer[0] <= '9')
        {
            return "_" + new string(buffer[..written]);
        }
        return new string(buffer[..written]);
    }

    private static Dictionary<string, string> BuildAuthorIndex(MaterialResourceCounts counts)
    {
        Dictionary<string, string> index = new(StringComparer.Ordinal);
        Add(index, "Texture2D", counts.Standard2D, counts.Standard2DAuthorNames);
        Add(index, "TextureCube", counts.Cube, counts.CubeAuthorNames);
        Add(index, "Texture2DArray", counts.Array2D, counts.Array2DAuthorNames);
        Add(index, "TextureCubeArray", counts.ArrayCube, counts.ArrayCubeAuthorNames);
        Add(index, "VolumeTexture", counts.Volume, counts.VolumeAuthorNames);
        Add(index, "ExternalTexture", counts.External, counts.ExternalAuthorNames);
        Add(index, "VirtualTexturePhysical", counts.Virtual, counts.VirtualAuthorNames);
        return index;

        static void Add(Dictionary<string, string> idx, string baseName, int count, IReadOnlyList<string?>? authorNames)
        {
            if (authorNames == null) return;
            for (int i = 0; i < count && i < authorNames.Count; i++)
            {
                string sanitized = SanitizeHlslIdent(authorNames[i]);
                if (sanitized.Length > 0)
                {
                    // Map author-name -> typed slot for both texture and sampler
                    idx[sanitized] = $"{baseName}_{i}";
                    idx[sanitized + "Sampler"] = $"{baseName}_{i}Sampler";
                }
            }
        }
    }

    private static void AppendVirtualTextureStacks(List<string> result, IReadOnlyList<int>? layerCountsPerStack)
    {
        if (layerCountsPerStack == null)
        {
            return;
        }

        for (int i = 0; i < layerCountsPerStack.Count; i++)
        {
            result.Add($"VirtualTexturePageTable0_{i}");
            if (layerCountsPerStack[i] > 4)
            {
                result.Add($"VirtualTexturePageTable1_{i}");
            }
            result.Add($"VirtualTexturePageTableIndirection_{i}");
        }
    }

    public sealed record MaterialResourceCounts(
        int Standard2D,
        int Cube,
        int Array2D,
        int ArrayCube,
        int Volume,
        int External,
        int Virtual,
        IReadOnlyList<int>? VirtualTextureStackLayerCounts,
        // Optional: total number of entries in
        // FRHIUniformBufferLayoutInitializer.Resources[]. When the unified
        // metadata path strips VTStacks, this lets us infer the VT stack
        // count by subtraction so the layout still resolves correctly.
        int? TotalResourceCount = null,
        // Per-typed-block author names from
        // UniformTextureParameters[Type][i].ParameterInfo.Name (or
        // ParameterName in the flattened unified-metadata shape). Each list
        // is parallel to the corresponding count: index `i` is the user-
        // facing name of the `i`-th texture in that typed block, or null /
        // "None" when the slot is anonymous (compiler-internal). When set,
        // the layout uses these to override the typed slot names like
        // `Texture2D_<i>` with the user-recognisable identifier so the
        // HLSL output reads as `Material_BambooBaseMaps` rather than
        // `Material_Texture2D_1`.
        IReadOnlyList<string?>? Standard2DAuthorNames = null,
        IReadOnlyList<string?>? CubeAuthorNames = null,
        IReadOnlyList<string?>? Array2DAuthorNames = null,
        IReadOnlyList<string?>? ArrayCubeAuthorNames = null,
        IReadOnlyList<string?>? VolumeAuthorNames = null,
        IReadOnlyList<string?>? ExternalAuthorNames = null,
        IReadOnlyList<string?>? VirtualAuthorNames = null);
}

// =====================================================================
// MaterialJsonSymbolReader
// =====================================================================
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

    public MaterialSymbolSource? GetBestSource(IEnumerable<string> materialPaths, string? shaderPlatform = null)
    {
        MaterialSymbolSource? best = null;
        foreach (string materialPath in materialPaths)
        {
            MaterialSymbolSource? candidate = GetSource(materialPath, shaderPlatform);
            if (candidate == null)
            {
                continue;
            }

            if (best == null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
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

        ShaderSymbolData metadata = SymbolBuilder.Build(inputs);
        string header = SymbolHeaderWriter.Build(inputs);
        int score = inputs.UsedLoadedMaterialResources ? 2 : inputs.NumericParameterInfos.Count > 0 ? 1 : 0;
        MaterialUniformBufferLayout? materialLayout = inputs.MaterialResourceCounts != null
            ? new MaterialUniformBufferLayout(inputs.MaterialResourceCounts)
            : null;
        MaterialSymbolSource source = new(normalizedPath, metadata, header, score, inputs.UsedLoadedMaterialResources, materialLayout);
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
}

// =====================================================================
// UnifiedMaterialReader
// =====================================================================
// Reads `MaterialInterfaces[<path>].LoadedShaderMaps[*].MaterialShaderMapContent.UniformExpressionSet`
// from a single `UnifiedShaderMetadata.json` so we can recover
// material-side names without the per-material `*.uasset.json` files
// FModel only writes when the user clicks Save Properties on every
// material. MaterialJsonSymbolReader is the per-material-JSON path; this
// reader is the unified-metadata path.
//
// Behaviour mirrors MaterialJsonSymbolReader: cache by (materialPath +
// shaderPlatform), return the same `MaterialSymbolSource` shape so
// downstream code is agnostic to which path served the lookup.
//
// Material lookup falls through several common path-spelling variants
// because the unified metadata's `MaterialInterfaces` keys are stored
// with a leading game-name segment (`Oni_Valley_VFX/Content/...`)
// while a shader's `UsedMaterials` list may already include or omit
// that segment depending on how the asset-info sidecars were merged.
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

    public MaterialSymbolSource? GetBestSource(IEnumerable<string> materialPaths, string? shaderPlatform = null)
    {
        MaterialSymbolSource? best = null;
        foreach (string materialPath in materialPaths)
        {
            MaterialSymbolSource? candidate = GetSource(materialPath, shaderPlatform);
            if (candidate == null)
            {
                continue;
            }

            if (best == null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
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

        JsonElement? uniformExpressionSet = SelectUniformExpressionSet(materialEntry, shaderPlatform);
        if (!uniformExpressionSet.HasValue)
        {
            _cache[cacheKey] = null;
            return null;
        }

        SymbolInputs? inputs = SymbolInputsReader.ReadFromUniformExpressionSet(normalizedPath, shaderPlatform, uniformExpressionSet.Value);
        if (inputs == null)
        {
            _cache[cacheKey] = null;
            return null;
        }

        ShaderSymbolData metadata = SymbolBuilder.Build(inputs);
        string header = SymbolHeaderWriter.Build(inputs);
        int score = inputs.UsedLoadedMaterialResources ? 2 : inputs.NumericParameterInfos.Count > 0 ? 1 : 0;
        MaterialUniformBufferLayout? materialLayout = inputs.MaterialResourceCounts != null
            ? new MaterialUniformBufferLayout(inputs.MaterialResourceCounts)
            : null;

        MaterialSymbolSource source = new(
            normalizedPath,
            metadata,
            header,
            score,
            inputs.UsedLoadedMaterialResources,
            materialLayout);
        _cache[cacheKey] = source;
        return source;
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

        // Strip object suffix: ".Material'..."` style or trailing ".N".
        int dotIndex = normalized.LastIndexOf('.');
        int slashIndex = normalized.LastIndexOf('/');
        if (dotIndex > slashIndex)
        {
            yield return normalized[..dotIndex];
        }

        // Drop a leading game-name / Content/ wrapper combination when
        // present — `MaterialInterfaces` keys are stored already
        // mount-point-relative.
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

// =====================================================================
// SymbolInputsReader
// =====================================================================
internal static class SymbolInputsReader
{
    public static SymbolInputs? Read(string materialPath, string? shaderPlatform, JsonElement asset)
    {
        SymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
        };

        JsonElement? selectedLoadedResource = SelectLoadedMaterialResource(asset, shaderPlatform, ref inputs);
        JsonElement? uniformExpressionSet = ResolveUniformExpressionSet(selectedLoadedResource);

        if (uniformExpressionSet.HasValue)
        {
            ReadUniformExpressionSet(inputs, uniformExpressionSet.Value);
        }

        ReadFallbackNumericParameters(asset, inputs.NumericParameterInfos);

        return inputs.NumericParameterInfos.Count == 0 && inputs.MaterialConstantBuffer == null
            ? null
            : inputs;
    }

    // Direct entry: caller already has the FUniformExpressionSet element
    // (e.g. from UnifiedShaderMetadata.json's
    // `MaterialInterfaces[<path>].LoadedShaderMaps[*].MaterialShaderMapContent.UniformExpressionSet`),
    // so we skip the per-material-asset wrapping and read the bridge
    // straight off it. `UsedLoadedMaterialResources` is forced true so
    // the source's score reflects that we picked a properly cooked
    // shader map.
    public static SymbolInputs? ReadFromUniformExpressionSet(string materialPath, string? shaderPlatform, JsonElement uniformExpressionSet)
    {
        SymbolInputs inputs = new()
        {
            MaterialPath = materialPath,
            ShaderPlatform = shaderPlatform,
            UsedLoadedMaterialResources = true,
        };

        ReadUniformExpressionSet(inputs, uniformExpressionSet);

        return inputs.NumericParameterInfos.Count == 0
               && inputs.MaterialConstantBuffer == null
               && inputs.MaterialResourceCounts == null
            ? null
            : inputs;
    }

    private static void ReadUniformExpressionSet(SymbolInputs inputs, JsonElement uniformExpressionSet)
    {
        inputs.MaterialConstantBuffer = ReadMaterialConstantBuffer(uniformExpressionSet);
        ReadUniformNumericParameters(uniformExpressionSet, inputs.NumericParameterInfos);
        inputs.MaterialResourceCounts = ReadMaterialResourceCounts(uniformExpressionSet);
    }

    private static JsonElement? SelectLoadedMaterialResource(JsonElement asset, string? shaderPlatform, ref SymbolInputs inputs)
    {
        if (!asset.TryGetProperty("LoadedMaterialResources", out JsonElement loadedResources) || loadedResources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) || loadedShaderMap.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? candidateShaderPlatform = ReadString(loadedShaderMap, "ShaderPlatform");
            if (!string.IsNullOrWhiteSpace(shaderPlatform) &&
                !string.Equals(candidateShaderPlatform, shaderPlatform, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            inputs.UsedLoadedMaterialResources = true;
            return resource.Clone();
        }

        foreach (JsonElement resource in loadedResources.EnumerateArray())
        {
            inputs.UsedLoadedMaterialResources = true;
            return resource.Clone();
        }

        return null;
    }

    private static JsonElement? ResolveUniformExpressionSet(JsonElement? loadedResource)
    {
        if (!loadedResource.HasValue)
        {
            return null;
        }

        JsonElement resource = loadedResource.Value;
        if (!resource.TryGetProperty("LoadedShaderMap", out JsonElement loadedShaderMap) || loadedShaderMap.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (loadedShaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement materialShaderMapContent) &&
            materialShaderMapContent.ValueKind == JsonValueKind.Object &&
            materialShaderMapContent.TryGetProperty("UniformExpressionSet", out JsonElement uniformExpressionSet))
        {
            return uniformExpressionSet.Clone();
        }

        if (loadedShaderMap.TryGetProperty("Content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("MaterialCompilationOutput", out JsonElement materialCompilationOutput) &&
            materialCompilationOutput.ValueKind == JsonValueKind.Object &&
            materialCompilationOutput.TryGetProperty("UniformExpressionSet", out JsonElement nestedUniformExpressionSet))
        {
            return nestedUniformExpressionSet.Clone();
        }

        return null;
    }

    private static ConstantBuffer? ReadMaterialConstantBuffer(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement uniformBufferLayoutInitializer) ||
            uniformBufferLayoutInitializer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? bufferName = ReadString(uniformBufferLayoutInitializer, "Name");
        if (!string.Equals(bufferName, "Material", StringComparison.Ordinal))
        {
            return null;
        }

        uint constantBufferSize = ReadUInt32(uniformBufferLayoutInitializer, "ConstantBufferSize");
        if (!uniformExpressionSet.TryGetProperty("UniformPreshaders", out JsonElement uniformPreshaders) ||
            uniformPreshaders.ValueKind != JsonValueKind.Array ||
            !uniformExpressionSet.TryGetProperty("UniformPreshaderFields", out JsonElement uniformPreshaderFields) ||
            uniformPreshaderFields.ValueKind != JsonValueKind.Array ||
            !uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement uniformNumericParameters) ||
            uniformNumericParameters.ValueKind != JsonValueKind.Array ||
            !uniformExpressionSet.TryGetProperty("UniformPreshaderData", out JsonElement uniformPreshaderData) ||
            uniformPreshaderData.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? encodedData = ReadString(uniformPreshaderData, "Data");
        if (string.IsNullOrWhiteSpace(encodedData))
        {
            return null;
        }

        byte[] opcodeData = Convert.FromBase64String(encodedData);
        ConstantBuffer materialBuffer = new()
        {
            Name = "Material",
            Size = checked((int)constantBufferSize)
        };

        // PreshaderField.BufferOffset is in float (4-byte) units relative to
        // the START of `PreshaderBuffer`, NOT to byte 0 of the Material UB.
        // CreateBufferStruct() (MaterialUniformExpressions.cpp:347-365) lays
        // out:
        //
        //     [VTPackedPageTableUniform : VTStacks.Num() * 2 * 16 bytes]   (uint4 array)
        //     [VTPackedUniform          : NumVirtualTextures * 16 bytes]   (uint4 array)
        //     [PreshaderBuffer          : UniformPreshaderBufferSize * 16] (float4 array)
        //     [Resources... (TEXTURE/SAMPLER/SRV pointers)]
        //
        // The HLSL cbuffer the shader compiles against contains all THREE
        // numeric blocks (VT page-table uniform, VT uniform, preshader),
        // so we need to model all three as members. PreshaderBuffer slots
        // come from our `UniformPreshaderFields` walk below; the two VT
        // blocks (sized in u4 multiples) are emitted up-front as named uint4
        // arrays so the shader's accesses to them resolve.
        (int preshaderBufferStart, int vtPageTableBytes, int vtUniformBytes) =
            ComputeNumericLayout(uniformExpressionSet, (int)constantBufferSize);

        // Walk every preshader. Each is a `Material` CB slot writer:
        //   field[FieldIndex] = evaluate(opcode stream)
        // The field record carries the authoritative (BufferOffset, Type) of
        // the slot; we honour that even when the opcode stream is too complex
        // to fully simulate. Naming is best-effort from the opcode stream:
        //   `Parameter(N)`  -> parameters[N].Name
        //   `Parameter(N) + ComponentSwizzle(...)` -> ParamName_<swizzle>
        //   `Parameter(N) + Saturate/Rcp/...` -> ParamName_<op>
        //   anything else -> ParamName_expr_<byteOffset> or `f_<byteOffset>`
        // Rationale: rewriter only needs (byteOffset, type) per slot to
        // expand cbuffer struct correctly; missing slots collapse the whole
        // CB to a single anonymous float4 array (the M_Bamboo_tree bug).
        HashSet<int> seenOffsets = new();
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        List<VectorParameter> vectorParams = new();
        List<MatrixParameter> matrixParams = new();

        if (vtPageTableBytes > 0)
        {
            // 2 * VTStacks.Num() uint4s starting at byte 0.
            vectorParams.Add(new VectorParameter
            {
                Name = "VTPackedPageTableUniform",
                NameIndex = -1,
                Type = ShaderParamType.UInt,
                ByteOffset = 0,
                ArraySize = vtPageTableBytes / 16,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
            seenOffsets.Add(0);
            seenNames.Add("VTPackedPageTableUniform");
        }
        if (vtUniformBytes > 0)
        {
            int vtUniformStart = vtPageTableBytes;
            vectorParams.Add(new VectorParameter
            {
                Name = "VTPackedUniform",
                NameIndex = -1,
                Type = ShaderParamType.UInt,
                ByteOffset = vtUniformStart,
                ArraySize = vtUniformBytes / 16,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
            seenOffsets.Add(vtUniformStart);
            seenNames.Add("VTPackedUniform");
        }
        foreach (JsonElement preshader in uniformPreshaders.EnumerateArray())
        {
            uint opcodeOffset = ReadUInt32(preshader, "OpcodeOffset");
            uint opcodeSize = ReadUInt32(preshader, "OpcodeSize");
            uint fieldIndex = ReadUInt32(preshader, "FieldIndex");
            uint numFields = ReadUInt32(preshader, "NumFields");

            // Only single-field-output preshaders for now. Multi-field writes
            // emerge for struct outputs and are uncommon for Material CBs;
            // adding them later requires walking each field's ComponentIndex
            // and Type independently.
            if (numFields != 1)
            {
                continue;
            }
            if (fieldIndex >= uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            JsonElement field = uniformPreshaderFields[checked((int)fieldIndex)];
            FieldKind kind = TryMapFieldType(ReadString(field, "Type"), out int rows);
            if (kind == FieldKind.Unknown)
            {
                continue;
            }

            int byteOffset = preshaderBufferStart + checked((int)ReadUInt32(field, "BufferOffset") * 4);
            if (!seenOffsets.Add(byteOffset))
            {
                continue;
            }

            string baseName = DerivePreshaderName(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset);

            // Most kinds emit one member at byteOffset.
            // LWC (Double*) emits TWO members: Tile + Offset, side-by-side, the
            // way HLSLMaterialTranslator.cpp:3322-3331 unpacks them at runtime
            // (`MakeLWCVector%d(Tile, Offset)` with Tile @ UniformOffset and
            // Offset @ UniformOffset + NumComponents). Treat them as
            // un-prefixed Float<N>s so the rewriter just sees back-to-back
            // float vectors covering the full byte range.
            switch (kind)
            {
                case FieldKind.Float:
                case FieldKind.Numeric:
                    AddVectorMember(vectorParams, seenNames, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, rows, ShaderParamType.Float);
                    break;
                case FieldKind.Int:
                    AddVectorMember(vectorParams, seenNames, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, rows, ShaderParamType.Int);
                    break;
                case FieldKind.Bool:
                    AddVectorMember(vectorParams, seenNames, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, rows, ShaderParamType.Bool);
                    break;
                case FieldKind.LwcDouble:
                    {
                        // LWC value occupies 2 * `rows` floats: Tile then
                        // Offset. The starting byteOffset is whatever
                        // BufferOffset says, which is NOT register-aligned in
                        // general (LWC slots can begin mid-register because
                        // HLSLMaterialTranslator advances UniformPreshaderOffset
                        // by `NumComponents * 2` after each LWC, not by a
                        // padded register count). So we cannot expose them as
                        // float<N> vectors -- HLSL packoffset rules forbid
                        // float3 starting at c<i>.w, etc. Emit each
                        // component as its own scalar member; the rewriter
                        // sees them as 2*rows back-to-back floats covering
                        // the full LWC byte range, and spirv-cross emits
                        // valid packoffset(c<i>.<comp>) for each.
                        int totalComponents = rows * 2;
                        for (int c = 0; c < totalComponents; c++)
                        {
                            int compOffset = byteOffset + c * 4;
                            if (c > 0)
                            {
                                seenOffsets.Add(compOffset);
                            }
                            string compName = c < rows
                                ? $"{baseName}_LwcTile_{"xyzw"[c]}"
                                : $"{baseName}_LwcOffset_{"xyzw"[c - rows]}";
                            AddVectorMember(vectorParams, seenNames, RegisterUniqueName(seenNames, compName, compOffset), compOffset, 1, ShaderParamType.Float);
                        }
                        break;
                    }
                case FieldKind.Float4x4:
                    AddMatrixMember(matrixParams, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, ShaderParamType.Float);
                    break;
                case FieldKind.LwcDouble4x4:
                    {
                        // Float4x4 is 64 bytes (4 columns of float4). LWC
                        // double4x4 is two of them back-to-back: Tile then
                        // Offset, 64 bytes apart.
                        int offsetPart = byteOffset + 64;
                        seenOffsets.Add(offsetPart);
                        AddMatrixMember(matrixParams, RegisterUniqueName(seenNames, $"{baseName}_LwcTile", byteOffset), byteOffset, ShaderParamType.Float);
                        AddMatrixMember(matrixParams, RegisterUniqueName(seenNames, $"{baseName}_LwcOffset", offsetPart), offsetPart, ShaderParamType.Float);
                        break;
                    }
            }
        }

        if (vectorParams.Count == 0 && matrixParams.Count == 0)
        {
            return null;
        }

        materialBuffer.VectorParams = vectorParams
            .OrderBy(static p => p.ByteOffset)
            .ToArray();
        materialBuffer.MatrixParams = matrixParams
            .OrderBy(static p => p.ByteOffset)
            .ToArray();
        return materialBuffer;
    }

    // Compute the three numeric blocks inside the Material UB:
    //   [VTPackedPageTableUniform : vtPageTableBytes] (uint4 array)
    //   [VTPackedUniform          : vtUniformBytes]   (uint4 array)
    //   [PreshaderBuffer          : preshaderBytes]   (float4 array starting at preshaderBufferStart)
    //
    // Source-of-truth split (per MaterialUniformExpressions.cpp:347-365):
    //   - PreshaderBuffer size is `UniformPreshaderBufferSize * 16` bytes
    //   - VTPackedUniform size is `Virtual_count * 16` bytes
    //   - VTPackedPageTableUniform size = (numericEnd - prevTwo) bytes
    //
    // numericEnd = `UniformBufferLayoutInitializer.Resources[0].MemberOffset`
    // when resources are present (start of the resource-pointer block), else
    // the full ConstantBufferSize. Virtual_count = length of
    // `UniformTextureParameters[Virtual]`.
    private static (int preshaderBufferStart, int vtPageTableBytes, int vtUniformBytes) ComputeNumericLayout(
        JsonElement uniformExpressionSet, int constantBufferSize)
    {
        int preshaderBufferSizeFloat4 = 0;
        if (uniformExpressionSet.TryGetProperty("UniformPreshaderBufferSize", out JsonElement sizeElement)
            && sizeElement.ValueKind == JsonValueKind.Number)
        {
            preshaderBufferSizeFloat4 = sizeElement.GetInt32();
        }
        int preshaderBufferBytes = Math.Max(0, preshaderBufferSizeFloat4) * 16;

        int numericEnd = constantBufferSize;
        if (uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement ubl)
            && ubl.ValueKind == JsonValueKind.Object
            && ubl.TryGetProperty("Resources", out JsonElement resources)
            && resources.ValueKind == JsonValueKind.Array
            && resources.GetArrayLength() > 0
            && resources[0].TryGetProperty("MemberOffset", out JsonElement firstResourceOffset)
            && firstResourceOffset.ValueKind == JsonValueKind.Number)
        {
            numericEnd = firstResourceOffset.GetInt32();
        }

        int virtualCount = 0;
        if (uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement textureParams)
            && textureParams.ValueKind == JsonValueKind.Array
            && textureParams.GetArrayLength() > 5
            && textureParams[5].ValueKind == JsonValueKind.Array)
        {
            virtualCount = textureParams[5].GetArrayLength();
        }
        int vtUniformBytes = virtualCount * 16;

        int vtPageTableBytes = numericEnd - preshaderBufferBytes - vtUniformBytes;
        if (vtPageTableBytes < 0)
        {
            vtPageTableBytes = 0;
        }

        int preshaderBufferStart = vtPageTableBytes + vtUniformBytes;
        return (preshaderBufferStart, vtPageTableBytes, vtUniformBytes);
    }

    private static string RegisterUniqueName(HashSet<string> seenNames, string candidate, int byteOffset)
    {
        if (seenNames.Add(candidate))
        {
            return candidate;
        }
        string disambiguated = $"{candidate}_at_{byteOffset}";
        seenNames.Add(disambiguated);
        return disambiguated;
    }

    private static void AddVectorMember(List<VectorParameter> destination, HashSet<string> _seenNames, string name, int byteOffset, int rows, ShaderParamType type)
    {
        destination.Add(new VectorParameter
        {
            Name = name,
            NameIndex = -1,
            Type = type,
            ByteOffset = byteOffset,
            ArraySize = 1,
            IsMatrix = false,
            RowCount = (byte)rows,
            ColumnCount = 1,
        });
    }

    private static void AddMatrixMember(List<MatrixParameter> destination, string name, int byteOffset, ShaderParamType type)
    {
        destination.Add(new MatrixParameter
        {
            Name = name,
            NameIndex = -1,
            Type = type,
            ByteOffset = byteOffset,
            ArraySize = 1,
            IsMatrix = true,
            RowCount = 4,
            ColumnCount = 4,
        });
    }

    private static string SwizzleSuffix(byte numE, byte r, byte g, byte b, byte a)
    {
        if (numE == 0 || numE > 4)
        {
            return string.Empty;
        }

        Span<byte> indices = stackalloc byte[4] { r, g, b, a };
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < numE; i++)
        {
            byte v = indices[i];
            char c = v switch
            {
                0 => 'x',
                1 => 'y',
                2 => 'z',
                3 => 'w',
                _ => '\0',
            };
            if (c == '\0')
            {
                return string.Empty;
            }
            chars[i] = c;
        }
        return new string(chars[..numE]);
    }

    // Name the cbuffer slot from the preshader opcode stream.
    //
    // Honesty rule: name the slot only if we can decode *every* byte of the
    // opcode stream into a closed-form description of what the runtime VM
    // writes into the slot. If any byte is unaccounted for (unrecognized
    // opcode, partial read, multi-Parameter expression we don't model), fall
    // back to anonymous `f_<byteOffset>`. This guarantees that any printed
    // name describes a value whose runtime byte content we can reproduce
    // exactly from public UE 5.1 source semantics — no guessing.
    //
    // Decoded forms:
    //   Parameter(N)                          -> parameters[N].Name
    //   Parameter(N) + ComponentSwizzle(..)   -> ParamName_<xyzw...>
    //   Parameter(N) + UnaryOp                -> ParamName_<op>
    //
    // EPreshaderOpcode reference: Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:19-75
    // (Parameter=3, Rcp=22, Saturate=25, Abs=26, Floor=27, Ceil=28, Round=29,
    //  Trunc=30, Sign=31, Frac=32, Fractional=33, ComponentSwizzle=36, Neg=45).
    // ComponentSwizzle payload: Engine/Source/Runtime/Engine/Private/Shader/Preshader.cpp:649-655
    // (uint8 NumElements, IndexR, IndexG, IndexB, IndexA).
    private static string DerivePreshaderName(
        byte[] data,
        uint offset,
        uint size,
        JsonElement parameters,
        int byteOffset)
    {
        string anonymous = $"f_{byteOffset}";

        // Must start with Parameter(N): exactly 1 + 2 = 3 bytes.
        if (size < 3 || offset >= (uint)data.Length || offset + 3 > (uint)data.Length)
        {
            return anonymous;
        }
        if (data[offset] != 3)
        {
            return anonymous;
        }

        ushort paramIdx = BitConverter.ToUInt16(data, checked((int)offset + 1));
        if (paramIdx >= parameters.GetArrayLength())
        {
            return anonymous;
        }

        FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[paramIdx]);
        if (info == null)
        {
            return anonymous;
        }
        string baseName = info.Name;

        // Pure Parameter(N) — slot is byte-equal to the parameter.
        if (size == 3)
        {
            return baseName;
        }

        // Parameter(N) + one trailing op that fully consumes the rest of the
        // opcode stream.
        int rest = checked((int)offset + 3);
        int restSize = checked((int)size) - 3;
        if (rest >= data.Length || restSize <= 0)
        {
            return anonymous;
        }
        byte tailOp = data[rest];

        // ComponentSwizzle: 1 op byte + 5 payload bytes (NumE, R, G, B, A).
        if (tailOp == 36 && restSize == 6 && rest + 6 <= data.Length)
        {
            byte numE = data[rest + 1];
            byte r = data[rest + 2];
            byte g = data[rest + 3];
            byte b = data[rest + 4];
            byte a = data[rest + 5];
            string swizzle = SwizzleSuffix(numE, r, g, b, a);
            if (!string.IsNullOrEmpty(swizzle))
            {
                return $"{baseName}_{swizzle}";
            }
            return anonymous;
        }

        // Unary in-place ops: 1 op byte and nothing else.
        if (restSize == 1)
        {
            string? unary = tailOp switch
            {
                22 => "rcp",
                25 => "sat",
                26 => "abs",
                27 => "floor",
                28 => "ceil",
                29 => "round",
                30 => "trunc",
                31 => "sign",
                32 => "frac",
                33 => "fractional",
                45 => "neg",
                _ => null,
            };
            if (unary != null)
            {
                return $"{baseName}_{unary}";
            }
        }

        // Anything else is a multi-step expression (Constants, Clamp, Append,
        // arithmetic, second Parameter pulls). We can't describe the slot's
        // runtime value in closed form -> anonymous.
        return anonymous;
    }

    private static MaterialUniformBufferLayout.MaterialResourceCounts? ReadMaterialResourceCounts(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement textureParams) || textureParams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // EMaterialTextureParameterType ordering matches FUniformExpressionSet::CreateBufferStruct:
        //   0 = Standard2D, 1 = Cube, 2 = Array2D, 3 = ArrayCube, 4 = Volume, 5 = Virtual.
        // External textures are a separate top-level array on the expression set.
        int Standard2D = ReadTypedArrayLength(textureParams, 0);
        int Cube = ReadTypedArrayLength(textureParams, 1);
        int Array2D = ReadTypedArrayLength(textureParams, 2);
        int ArrayCube = ReadTypedArrayLength(textureParams, 3);
        int Volume = ReadTypedArrayLength(textureParams, 4);
        int Virtual = ReadTypedArrayLength(textureParams, 5);

        int External = 0;
        if (uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement externalParams) && externalParams.ValueKind == JsonValueKind.Array)
        {
            External = externalParams.GetArrayLength();
        }

        // VTStack page tables are independent of UniformTextureParameters[Virtual].
        // Each FMaterialVirtualTextureStack carries its own NumLayers, which gates
        // whether a 5th-8th layer page table (VirtualTexturePageTable1_<i>) is
        // emitted in addition to PageTable0/Indirection. We need the per-stack
        // layer count, not just the stack count.
        List<int>? vtStackLayers = null;
        if (uniformExpressionSet.TryGetProperty("VTStacks", out JsonElement vtStacks) && vtStacks.ValueKind == JsonValueKind.Array)
        {
            vtStackLayers = new List<int>(vtStacks.GetArrayLength());
            foreach (JsonElement stack in vtStacks.EnumerateArray())
            {
                vtStackLayers.Add(ReadVirtualTextureStackNumLayers(stack));
            }
        }

        // Read the actual Resources[] length so the layout helper can infer
        // VTStack count when the JSON shape (e.g. UnifiedShaderMetadata) does
        // not carry the VTStacks array directly.
        int? totalResources = null;
        if (uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement ubl)
            && ubl.ValueKind == JsonValueKind.Object
            && ubl.TryGetProperty("Resources", out JsonElement resources)
            && resources.ValueKind == JsonValueKind.Array)
        {
            totalResources = resources.GetArrayLength();
        }

        // Per-typed-block author-facing texture parameter names. We read
        // ParameterInfo.Name for each typed slot so the layout can replace
        // `Texture2D_<i>` with the user-recognisable identifier.
        IReadOnlyList<string?>? std2dNames = ReadTextureAuthorNames(textureParams, 0);
        IReadOnlyList<string?>? cubeNames = ReadTextureAuthorNames(textureParams, 1);
        IReadOnlyList<string?>? a2dNames = ReadTextureAuthorNames(textureParams, 2);
        IReadOnlyList<string?>? acubeNames = ReadTextureAuthorNames(textureParams, 3);
        IReadOnlyList<string?>? volNames = ReadTextureAuthorNames(textureParams, 4);
        IReadOnlyList<string?>? virtNames = ReadTextureAuthorNames(textureParams, 5);
        IReadOnlyList<string?>? extNames = ReadExternalAuthorNames(uniformExpressionSet);

        return new MaterialUniformBufferLayout.MaterialResourceCounts(
            Standard2D: Standard2D,
            Cube: Cube,
            Array2D: Array2D,
            ArrayCube: ArrayCube,
            Volume: Volume,
            External: External,
            Virtual: Virtual,
            VirtualTextureStackLayerCounts: vtStackLayers,
            TotalResourceCount: totalResources,
            Standard2DAuthorNames: std2dNames,
            CubeAuthorNames: cubeNames,
            Array2DAuthorNames: a2dNames,
            ArrayCubeAuthorNames: acubeNames,
            VolumeAuthorNames: volNames,
            ExternalAuthorNames: extNames,
            VirtualAuthorNames: virtNames);
    }

    private static IReadOnlyList<string?>? ReadTextureAuthorNames(JsonElement arrayOfArrays, int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= arrayOfArrays.GetArrayLength())
        {
            return null;
        }

        JsonElement inner = arrayOfArrays[typeIndex];
        if (inner.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string?> names = new(inner.GetArrayLength());
        foreach (JsonElement entry in inner.EnumerateArray())
        {
            FMaterialParameterInfo? info = ParseMaterialParameterInfo(entry);
            names.Add(info?.Name);
        }
        return names;
    }

    private static IReadOnlyList<string?>? ReadExternalAuthorNames(JsonElement uniformExpressionSet)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformExternalTextureParameters", out JsonElement external)
            || external.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string?> names = new(external.GetArrayLength());
        foreach (JsonElement entry in external.EnumerateArray())
        {
            FMaterialParameterInfo? info = ParseMaterialParameterInfo(entry);
            names.Add(info?.Name);
        }
        return names;
    }

    // FMaterialVirtualTextureStack stores LayerUniformExpressionIndices as an
    // 8-element fixed array; "NumLayers" is the count of indices that are not
    // INDEX_NONE. The shape in FModel/CUE4Parse JSON varies, so probe a few
    // common forms; if none match we conservatively assume <=4 layers (no
    // PageTable1_<i> entry).
    private static int ReadVirtualTextureStackNumLayers(JsonElement stack)
    {
        if (stack.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (stack.TryGetProperty("NumLayers", out JsonElement numLayers) && numLayers.ValueKind == JsonValueKind.Number)
        {
            return numLayers.GetInt32();
        }

        if (stack.TryGetProperty("LayerUniformExpressionIndices", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (JsonElement element in layers.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                int value = element.GetInt32();
                if (value >= 0)
                {
                    count++;
                }
            }
            return count;
        }

        return 0;
    }

    private static int ReadTypedArrayLength(JsonElement arrayOfArrays, int index)
    {
        if (index < 0 || index >= arrayOfArrays.GetArrayLength())
        {
            return 0;
        }

        JsonElement inner = arrayOfArrays[index];
        return inner.ValueKind == JsonValueKind.Array ? inner.GetArrayLength() : 0;
    }

    private static void ReadUniformNumericParameters(JsonElement uniformExpressionSet, List<FMaterialParameterInfo> destination)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement numericParameters) || numericParameters.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement parameter in numericParameters.EnumerateArray())
        {
            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(parameter);
            if (parameterInfo != null)
            {
                destination.Add(parameterInfo);
            }
        }
    }

    private static void ReadFallbackNumericParameters(JsonElement asset, List<FMaterialParameterInfo> destination)
    {
        if (!asset.TryGetProperty("Properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        AppendMaterialParameterInfos(properties, "ScalarParameterValues", destination);
        AppendMaterialParameterInfos(properties, "VectorParameterValues", destination);
        AppendMaterialParameterInfos(properties, "DoubleVectorParameterValues", destination);
    }

    private static void AppendMaterialParameterInfos(JsonElement properties, string propertyName, List<FMaterialParameterInfo> destination)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            FMaterialParameterInfo? parameterInfo = ParseMaterialParameterInfo(entry);
            if (parameterInfo != null)
            {
                destination.Add(parameterInfo);
            }
        }
    }

    // UE 5.1 Engine/Source/Runtime/Engine/Public/Shader/ShaderTypes.h:93-139
    // EValueType. We need to know the **scalar component count** of every
    // type that can appear as a UniformPreshaderField, plus the special LWC
    // (Double*) shape because UE encodes those as Tile+Offset float pairs in
    // the cbuffer (HLSLMaterialTranslator.cpp:3293-3308: `bIsLWC ? Double :
    // Float` component type, with `UniformPreshaderOffset += bIsLWC ?
    // NumComponents * 2u : NumComponents` -> the field reserves 2*N float
    // slots starting at BufferOffset).
    private enum FieldKind { Unknown, Float, LwcDouble, Int, Bool, Numeric, Float4x4, LwcDouble4x4 }

    private static FieldKind TryMapFieldType(string? fieldType, out int rows)
    {
        rows = 0;
        switch (fieldType)
        {
            case "Float1": rows = 1; return FieldKind.Float;
            case "Float2": rows = 2; return FieldKind.Float;
            case "Float3": rows = 3; return FieldKind.Float;
            case "Float4": rows = 4; return FieldKind.Float;

            case "Double1": rows = 1; return FieldKind.LwcDouble;
            case "Double2": rows = 2; return FieldKind.LwcDouble;
            case "Double3": rows = 3; return FieldKind.LwcDouble;
            case "Double4": rows = 4; return FieldKind.LwcDouble;

            case "Int1": rows = 1; return FieldKind.Int;
            case "Int2": rows = 2; return FieldKind.Int;
            case "Int3": rows = 3; return FieldKind.Int;
            case "Int4": rows = 4; return FieldKind.Int;

            case "Bool1": rows = 1; return FieldKind.Bool;
            case "Bool2": rows = 2; return FieldKind.Bool;
            case "Bool3": rows = 3; return FieldKind.Bool;
            case "Bool4": rows = 4; return FieldKind.Bool;

            // EValueType::Numeric* is a generic placeholder that resolves to
            // Float at evaluation time; HLSLMaterialTranslator never emits it
            // as a buffer field type but we accept it as Float defensively.
            case "Numeric1": rows = 1; return FieldKind.Numeric;
            case "Numeric2": rows = 2; return FieldKind.Numeric;
            case "Numeric3": rows = 3; return FieldKind.Numeric;
            case "Numeric4": rows = 4; return FieldKind.Numeric;

            case "Float4x4": rows = 4; return FieldKind.Float4x4;
            case "Double4x4": rows = 4; return FieldKind.LwcDouble4x4;

            default: return FieldKind.Unknown;
        }
    }

    private static FMaterialParameterInfo? ParseMaterialParameterInfo(JsonElement element)
    {
        // Accept both shapes:
        //   * per-material `.uasset.json` (FModel "Save Properties"):
        //     `{ "ParameterInfo": { "Name": "...", "Association": "...", "Index": ... }, ... }`
        //   * UnifiedShaderMetadata.json (Ruri.FModelHook hook output):
        //     flattened `{ "ParameterName": "...", "Association": "...", "Index": ..., ... }`
        JsonElement parameterInfo;
        bool nested;
        if (element.TryGetProperty("ParameterInfo", out parameterInfo) && parameterInfo.ValueKind == JsonValueKind.Object)
        {
            nested = true;
        }
        else
        {
            parameterInfo = element;
            nested = false;
        }

        string? name = nested
            ? ReadString(parameterInfo, "Name")
            : (ReadString(parameterInfo, "ParameterName") ?? ReadString(parameterInfo, "Name"));
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? associationRaw = ReadString(parameterInfo, "Association");
        EMaterialParameterAssociation association = associationRaw switch
        {
            "EMaterialParameterAssociation::LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "EMaterialParameterAssociation::BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            "LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            _ => EMaterialParameterAssociation.GlobalParameter
        };

        int index = parameterInfo.TryGetProperty("Index", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : -1;
        return new FMaterialParameterInfo(name, association, index);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static uint ReadUInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"Missing numeric property: {propertyName}");
        }

        return value.GetUInt32();
    }
}

// =====================================================================
// SymbolBuilder + SymbolHeaderWriter + SymbolInputs
// =====================================================================
internal static class SymbolBuilder
{
    public static ShaderSymbolData Build(SymbolInputs inputs)
    {
        ShaderSymbolData metadata = new()
        {
            DebugName = inputs.MaterialPath
        };

        if (inputs.MaterialConstantBuffer != null)
        {
            metadata.ConstantBuffers.Add(inputs.MaterialConstantBuffer);
        }

        metadata.ConstantBuffers = metadata.ConstantBuffers
            .GroupBy(static buffer => buffer.Name, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
        return metadata;
    }
}

internal static class SymbolHeaderWriter
{
    public static string Build(SymbolInputs inputs)
    {
        StringBuilder sb = new();
        sb.AppendLine("/*");
        sb.AppendLine(" * UE Shader Symbol Inputs");
        sb.AppendLine($" * Material: {inputs.MaterialPath}");
        if (!string.IsNullOrWhiteSpace(inputs.ShaderPlatform))
        {
            sb.AppendLine($" * ShaderPlatform: {inputs.ShaderPlatform}");
        }

        sb.AppendLine($" * Source: {(inputs.UsedLoadedMaterialResources ? "LoadedMaterialResources.UniformExpressionSet" : "Material Properties Fallback")}");
        sb.AppendLine($" * NumericParameters: {inputs.NumericParameterInfos.Count}");
        sb.AppendLine($" * MaterialConstantBuffer: {(inputs.MaterialConstantBuffer != null ? "present" : "absent")}");

        foreach (FMaterialParameterInfo parameterInfo in inputs.NumericParameterInfos
                     .GroupBy(static info => $"{info.Name}|{info.Association}|{info.Index}", StringComparer.Ordinal)
                     .Select(static group => group.First())
                     .Take(16))
        {
            sb.AppendLine($" * Parameter: Name={parameterInfo.Name}, Association={parameterInfo.Association}, Index={parameterInfo.Index}");
        }

        if (inputs.MaterialConstantBuffer != null)
        {
            foreach (NumericShaderParameter parameter in inputs.MaterialConstantBuffer.AllNumericParams
                         .OrderBy(static p => p.ByteOffset)
                         .Take(32))
            {
                sb.AppendLine($" * MaterialCB: {parameter.Name} @ byte {parameter.ByteOffset} rows={parameter.RowCount} cols={parameter.ColumnCount}");
            }
        }

        sb.AppendLine(" */");
        sb.AppendLine();
        return sb.ToString();
    }
}

internal sealed class SymbolInputs
{
    public string MaterialPath { get; set; } = string.Empty;
    public string? ShaderPlatform { get; set; }
    public bool UsedLoadedMaterialResources { get; set; }
    public ConstantBuffer? MaterialConstantBuffer { get; set; }
    public List<FMaterialParameterInfo> NumericParameterInfos { get; } = new();
    public MaterialUniformBufferLayout.MaterialResourceCounts? MaterialResourceCounts { get; set; }
}
