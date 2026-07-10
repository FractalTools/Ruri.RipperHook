using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ruri.Hook.Core;
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

        // Pick the asset entry that actually carries material symbols.
        // `root[0]` is the right pick for a plain material/material-instance
        // package (FMaterial / UMaterialInstanceConstant sit at index 0).
        // But level/landscape packages (e.g. `_Generated_/MainGrid_L2_*`)
        // have `LandscapeComponent` at index 0 with the material symbols
        // hiding on later `LandscapeMaterialInstanceConstant` entries — we
        // pick the FIRST entry whose `LoadedMaterialResources` is non-empty.
        // First-wins is a coarse heuristic: each landscape file holds N
        // instances (one per cell) with slightly different parameter
        // overrides. The names (which is what symbol recovery needs) are
        // identical across instances; only the override VALUES differ.
        JsonElement materialAsset = SelectMaterialAsset(root);
        SymbolInputs? inputs = SymbolInputsReader.Read(normalizedPath, shaderPlatform, materialAsset);
        if (inputs == null)
        {
            _cache[cacheKey] = null;
            return null;
        }

        // Resolve `MaterialCollection<i>` cbuffers from the material's
        // referenced UMaterialParameterCollection assets — these aren't in the
        // Material UB itself, they're separate bindings that previously
        // collapsed to anonymous `_m0[N]` flat arrays.
        MaterialParameterCollectionReader.ResolveAndInject(materialAsset, inputs, _exportRoot, _exportRootName);

        MaterialSymbolSource source = BuildSource(normalizedPath, inputs);
        _cache[cacheKey] = source;
        return source;
    }

    // Pick the JSON-array entry that owns the material symbols. For a
    // plain material package this is just `root[0]`. For level packages
    // (landscape-instance assets in particular) the LANDSCAPE COMPONENT
    // sits at index 0 with the actual material data on later
    // `LandscapeMaterialInstanceConstant` entries — fall back to the
    // first entry that has a non-empty `LoadedMaterialResources` array.
    private static JsonElement SelectMaterialAsset(JsonElement root)
    {
        if (HasLoadedMaterialResources(root[0]))
        {
            return root[0];
        }
        foreach (JsonElement entry in root.EnumerateArray())
        {
            if (HasLoadedMaterialResources(entry))
            {
                return entry;
            }
        }
        return root[0];
    }

    private static bool HasLoadedMaterialResources(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return false;
        if (!entry.TryGetProperty("LoadedMaterialResources", out JsonElement loaded)) return false;
        return loaded.ValueKind == JsonValueKind.Array && loaded.GetArrayLength() > 0;
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

    // Above this on-disk size the unified file is NOT loaded into a JsonDocument
    // for per-material symbol lookup. Rationale: this reader holds the ENTIRE
    // parsed document in memory for the session, and `JsonDocument` is backed by
    // a single contiguous buffer that hits .NET's ~2GB array ceiling — a cook
    // that references every material (the master archive, 23k materials) yields a
    // ~3GB unified that can't be materialised at all (observed "Insufficient
    // memory", which then starved the dxil-spirv native and failed the whole
    // decompile). Past the cap we skip the rich symbol source and let naming fall
    // back to the per-archive `.assetinfo.json` sidecar + the lean hash bridges.
    // Archive-scoped exports (the common case) stay well under this and get full
    // symbols. TODO(top-tier): replace the whole-document hold with an on-disk
    // seek index so per-material symbols load on demand regardless of cook size.
    private const long MaxInMemoryUnifiedBytes = 1024L * 1024 * 1024; // 1 GiB

    public static UnifiedMaterialReader? LoadFromFile(string unifiedMetadataPath)
    {
        if (string.IsNullOrWhiteSpace(unifiedMetadataPath) || !File.Exists(unifiedMetadataPath))
        {
            return null;
        }

        long length = new FileInfo(unifiedMetadataPath).Length;
        if (length > MaxInMemoryUnifiedBytes)
        {
            HookLogger.LogWarning($"[UnifiedMaterialReader] Unified metadata is {length / (1024 * 1024)} MB (> {MaxInMemoryUnifiedBytes / (1024 * 1024)} MB cap) — skipping the in-memory symbol source to avoid the 2GB JsonDocument limit. Material NAMES still resolve from sidecars; per-material rich symbols are unavailable for this all-materials cache. Export a narrower archive set for full symbols.");
            return null;
        }

        try
        {
            // Stream the bytes (UTF-8) straight into JsonDocument instead of
            // File.ReadAllText — the latter builds a UTF-16 string first and
            // throws past ~1GB of text well before the file hits the cap above.
            using FileStream stream = File.OpenRead(unifiedMetadataPath);
            JsonDocument document = JsonDocument.Parse(stream);
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

    // Iterates every (libraryShaderMapHash, ParameterMapInfo-by-ResourceIndex)
    // tuple across every material. The on-disk LIBRARY hash is what
    // `ShaderMapInfo.ShaderMapHash` carries — it's NOT the cook-internal
    // `CookedShaderMapIdHash` (those diverge for IoStore cooks; see
    // `UnifiedShaderMapMetadata.ResourceHash`'s doc comment). Resolution order
    // per shader-map entry:
    //   1. `ResourceHash` — the SAME field the material-linking bridge
    //      (Pass 030 Tier 1/Tier 2, Pass 050) already treats as authoritative:
    //      it IS the archive's `ShaderMapHashes` value for bShareCode cooks,
    //      independent of array position. Correct for every IoStore cook.
    //   2. `PackageShaderMapHashes[i]` (positional pairing with
    //      `LoadedShaderMaps[i]`) — kept as a fallback for shader-maps whose
    //      `ResourceHash` didn't survive extraction, but this pairing is only
    //      as reliable as UE's array-order guarantee between the two lists.
    //   3. `CookedShaderMapIdHash` / `ShaderContentHash` — last resort, only
    //      matches non-IoStore cooks where the internal and on-disk hashes
    //      happen to agree.
    //
    // Per-shader lookup is keyed by `ResourceIndex` (the shader's cooker-
    // assigned slot within its owning shader-map, 0..NumShaders-1) rather than
    // walking the JSON arrays by POSITION. This matters because a bShareCode
    // material's base `MaterialShaderMapContent.Shaders[]` is genuinely empty
    // — verified empirically, the frozen memory image is real (20-38KB, not
    // truncated) but UE nests every actual VS/PS/etc under
    // `OrderedMeshShaderMaps[i].Shaders[]` (one bucket per vertex-factory
    // permutation) instead. Concatenating those buckets by ARRAY POSITION
    // would only accidentally line up with the archive's own per-map ordering;
    // `ResourceIndex` is the value both sides actually agree on —
    // `ShaderMapMember.RelativeIndex`'s own doc comment already states
    // "0..NumShaders-1, == metadata ResourceIndex". Folding both `Shaders[]`
    // and every `OrderedMeshShaderMaps[i].Shaders[]` bucket into ONE
    // ResourceIndex-keyed dictionary makes the join correct regardless of
    // which bucket a cook happens to populate.
    public IEnumerable<(string LibraryShaderMapHash, Dictionary<int, JsonElement> ParameterMapInfoByResourceIndex)> EnumerateShaderMapShaders()
    {
        if (_materialInterfaces == null) yield break;
        foreach (KeyValuePair<string, JsonElement> kvp in _materialInterfaces)
        {
            JsonElement materialEntry = kvp.Value;
            if (!materialEntry.TryGetProperty("LoadedShaderMaps", out JsonElement loadedMaps)
                || loadedMaps.ValueKind != JsonValueKind.Array
                || loadedMaps.GetArrayLength() == 0)
            {
                continue;
            }
            // PackageShaderMapHashes is OPTIONAL (older cooks don't write it
            // per-material) and only used as a positional fallback below.
            List<string?> packageHashes = new();
            if (materialEntry.TryGetProperty("PackageShaderMapHashes", out JsonElement pkgHashes)
                && pkgHashes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement h in pkgHashes.EnumerateArray())
                {
                    packageHashes.Add(h.ValueKind == JsonValueKind.String ? h.GetString() : null);
                }
            }
            int i = 0;
            foreach (JsonElement shaderMap in loadedMaps.EnumerateArray())
            {
                if (shaderMap.ValueKind != JsonValueKind.Object) { i++; continue; }
                string? libraryHash = ReadString(shaderMap, "ResourceHash");
                if (string.IsNullOrWhiteSpace(libraryHash))
                {
                    libraryHash = i < packageHashes.Count ? packageHashes[i] : null;
                }
                if (string.IsNullOrWhiteSpace(libraryHash))
                {
                    libraryHash = ReadString(shaderMap, "CookedShaderMapIdHash")
                                  ?? ReadString(shaderMap, "ShaderContentHash");
                }
                i++;
                if (string.IsNullOrWhiteSpace(libraryHash)) continue;
                if (!shaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement content)
                    || content.ValueKind != JsonValueKind.Object) continue;

                var byResourceIndex = new Dictionary<int, JsonElement>();
                CollectShadersByResourceIndex(content, byResourceIndex);
                if (content.TryGetProperty("OrderedMeshShaderMaps", out JsonElement meshMaps)
                    && meshMaps.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement meshMap in meshMaps.EnumerateArray())
                    {
                        if (meshMap.ValueKind == JsonValueKind.Object) CollectShadersByResourceIndex(meshMap, byResourceIndex);
                    }
                }
                if (byResourceIndex.Count == 0) continue;
                yield return (libraryHash, byResourceIndex);
            }
        }
    }

    // Reads a container's `Shaders[]` array (works for both the top-level
    // `MaterialShaderMapContent` and each `OrderedMeshShaderMaps[i]` entry —
    // both carry a `Shaders` property of the same shape) and indexes every
    // entry's `ParameterMapInfo` by its `ResourceIndex`. Shaders without a
    // `ParameterMapInfo` (e.g. a placeholder for an unfrozen pointer slot) are
    // skipped, not added as an empty entry.
    private static void CollectShadersByResourceIndex(JsonElement container, Dictionary<int, JsonElement> result)
    {
        if (!container.TryGetProperty("Shaders", out JsonElement shaders) || shaders.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement shader in shaders.EnumerateArray())
        {
            if (shader.ValueKind != JsonValueKind.Object) continue;
            if (!shader.TryGetProperty("ResourceIndex", out JsonElement riEl) || riEl.ValueKind != JsonValueKind.Number) continue;
            if (!shader.TryGetProperty("ParameterMapInfo", out JsonElement pmi) || pmi.ValueKind != JsonValueKind.Object) continue;
            result[riEl.GetInt32()] = pmi;
        }
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

        // CRITICAL — do NOT synthesise a numeric Material cbuffer from
        // CachedParameters. CachedExpressionData carries parameter NAMES but
        // NO byte offsets (those live only in the UniformExpressionSet, which
        // this cook strips — LoadedShaderMaps is empty for ~all materials). The
        // old behaviour placed each name at a guessed slot*16 offset and typed
        // every scalar as float4; the rewriter then PINNED those guesses onto
        // the flat `Material_m0[N]` whenever the synthetic offsets happened to
        // pass access-chain validation — emitting WRONG names/offsets/types
        // (e.g. the scalar `RefractionDepthBias` rendered `float4 ... : packoffset(c0)`).
        // That is precisely the "metadata that doesn't correspond, forced onto
        // the cb" failure mode. A guessed Material cb is worse than an honest
        // anonymous `Material_loose[N]`, so we emit NONE here: numeric Material
        // members are named ONLY through the byte-offset-accurate UES path
        // (UnifiedMaterialReader Path 1 / MaterialConstantBufferReader). Texture
        // names above are safe — the patcher matches them by bind index, not
        // offset — so they stay.
        if (metadata.TextureParameters.Count == 0)
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
