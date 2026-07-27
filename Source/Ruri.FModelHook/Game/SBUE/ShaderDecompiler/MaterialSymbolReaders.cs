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

    // ---- On-disk seek-index mode (files past MaxInMemoryUnifiedBytes) ----
    // `_index` maps NormalizeKey(materialPath) -> the byte range of that material's
    // JSON value inside `MaterialInterfaces`. Lookups seek + parse ONE material,
    // so peak memory is bounded by the single largest material entry instead of the
    // whole cook. Symbols are therefore available at ANY cook size — the old
    // behaviour (return null past the cap) silently downgraded every Material cb to
    // anonymous `Material_loose[N]` on all-materials caches, which is exactly the
    // "symbols must all be restored" red line.
    private readonly string? _indexedPath;
    private readonly Dictionary<string, MaterialEntryRange>? _index;

    private readonly record struct MaterialEntryRange(long Start, int Length);

    private UnifiedMaterialReader(JsonDocument document, Dictionary<string, JsonElement> materialInterfaces)
    {
        _document = document;
        _materialInterfaces = materialInterfaces;
    }

    private UnifiedMaterialReader(string indexedPath, Dictionary<string, MaterialEntryRange> index)
    {
        _indexedPath = indexedPath;
        _index = index;
    }

    /// <summary>是否有可用的符号源(内存模式或索引模式其一)。</summary>
    private bool HasSource => _materialInterfaces != null || _index != null;

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
    // symbols. Past the cap we no longer give up: `BuildMaterialInterfaceIndex`
    // streams the file once and records each material's byte range, and lookups
    // seek+parse a single entry on demand (bounded memory, symbols at any size).
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
            // Too big for a single JsonDocument (contiguous buffer, ~2GB array ceiling)
            // → build an on-disk seek index instead of dropping the symbol source.
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<string, MaterialEntryRange>? index;
            try
            {
                index = BuildMaterialInterfaceIndex(unifiedMetadataPath);
            }
            catch (Exception ex)
            {
                HookLogger.LogWarning($"[UnifiedMaterialReader] Seek-index build failed on {length / (1024 * 1024)} MB unified metadata: {ex.GetType().Name}: {ex.Message} — per-material symbols unavailable.");
                return null;
            }
            sw.Stop();
            if (index == null || index.Count == 0)
            {
                HookLogger.LogWarning($"[UnifiedMaterialReader] Unified metadata {length / (1024 * 1024)} MB has no usable MaterialInterfaces block — per-material symbols unavailable.");
                return null;
            }
            HookLogger.Log($"[UnifiedMaterialReader] Unified metadata is {length / (1024 * 1024)} MB (> {MaxInMemoryUnifiedBytes / (1024 * 1024)} MB) — built on-disk seek index: {index.Count} material(s) in {sw.ElapsedMilliseconds} ms; per-material symbols load on demand.");
            return new UnifiedMaterialReader(unifiedMetadataPath, index);
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

    // Streams the unified metadata once with Utf8JsonReader and records the byte
    // range of every value under `MaterialInterfaces`. Stops as soon as that object
    // closes — the block sits near the file end on big cooks, but the scan is a
    // single sequential pass either way and never materialises a value.
    //
    // Buffer management is the standard chunked-Utf8JsonReader pattern: the reader
    // works on a span, so unconsumed bytes are compacted to the front and the buffer
    // grows whenever a single value (a material entry can be MBs) doesn't fit.
    private static Dictionary<string, MaterialEntryRange>? BuildMaterialInterfaceIndex(string path)
    {
        var index = new Dictionary<string, MaterialEntryRange>(StringComparer.OrdinalIgnoreCase);
        using FileStream stream = File.OpenRead(path);

        byte[] buffer = new byte[1 << 20];
        int dataLength = stream.Read(buffer, 0, buffer.Length);
        bool isFinalBlock = dataLength < buffer.Length;
        long bufferStartOffset = 0;                 // file offset of buffer[0]
        JsonReaderState state = new(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        bool insideMaterialInterfaces = false;
        // Depth of the PROPERTY NAMES inside the MaterialInterfaces object.
        // Utf8JsonReader.CurrentDepth for a StartObject is the depth of the container
        // itself, and its member property names sit one level deeper — so the inner
        // depth is (StartObject depth + 1), and the object's own EndObject comes back
        // at (inner - 1). Getting this off by one indexes ROOT-level siblings instead
        // (e.g. the bool `MaterialScanComplete`), which then blows up as "requires an
        // element of type 'Object', but the target element has type 'True'".
        int materialInterfacesInnerDepth = -1;
        bool pendingMaterialInterfaces = false;     // saw the property name, next token is its value
        string? pendingMaterialName = null;
        bool done = false;

        while (!done)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, dataLength), isFinalBlock, state);
            bool needMoreData = false;
            // Resume checkpoint = (bytes consumed, reader state) captured BEFORE each
            // token is read. A material entry is handled as a PropertyName + value
            // PAIR across two Read() calls, so when the value doesn't fit in the
            // current buffer we must rewind to before the NAME — resuming from the
            // post-name position would re-enter the loop pointing at the value's
            // first inner token and the whole entry would be walked as ordinary
            // tokens and silently dropped from the index (measured: 3306 of 6357).
            long resumeConsumed = 0;
            JsonReaderState resumeState = state;

            while (true)
            {
                resumeConsumed = reader.BytesConsumed;
                resumeState = reader.CurrentState;
                if (!reader.Read()) { needMoreData = !isFinalBlock; break; }

                if (pendingMaterialInterfaces)
                {
                    pendingMaterialInterfaces = false;
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        insideMaterialInterfaces = true;
                        materialInterfacesInnerDepth = reader.CurrentDepth + 1;
                        continue;
                    }
                    // Not an object → nothing to index.
                    if (!reader.TrySkip()) { needMoreData = true; break; }
                    continue;
                }

                if (!insideMaterialInterfaces)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName
                        && reader.CurrentDepth == 1
                        && reader.ValueTextEquals("MaterialInterfaces"))
                    {
                        pendingMaterialInterfaces = true;
                    }
                    continue;
                }

                // Inside MaterialInterfaces.
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == materialInterfacesInnerDepth - 1)
                {
                    done = true;
                    break;
                }
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != materialInterfacesInnerDepth)
                {
                    continue;
                }

                pendingMaterialName = reader.GetString();
                if (!reader.Read()) { needMoreData = !isFinalBlock; break; }

                bool isObjectValue = reader.TokenType == JsonTokenType.StartObject;
                long valueStart = bufferStartOffset + reader.TokenStartIndex;
                if (!reader.TrySkip()) { needMoreData = true; break; }
                long valueEnd = bufferStartOffset + reader.BytesConsumed;

                // Only object entries are material records; anything else under this
                // block would be a schema change, and indexing it would hand a
                // non-object to every downstream TryGetProperty.
                if (isObjectValue && !string.IsNullOrEmpty(pendingMaterialName) && valueEnd > valueStart && valueEnd - valueStart < int.MaxValue)
                {
                    index[NormalizeKey(pendingMaterialName!)] = new MaterialEntryRange(valueStart, (int)(valueEnd - valueStart));
                }
                pendingMaterialName = null;
            }

            int consumed = needMoreData ? (int)resumeConsumed : (int)reader.BytesConsumed;
            state = needMoreData ? resumeState : reader.CurrentState;
            if (done) break;

            if (!needMoreData && isFinalBlock) break;

            bufferStartOffset += consumed;
            int leftover = dataLength - consumed;
            if (leftover > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, leftover);
            if (leftover == buffer.Length)
            {
                // A single token/value spans the whole buffer — grow so TrySkip can finish.
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            int read = stream.Read(buffer, leftover, buffer.Length - leftover);
            dataLength = leftover + read;
            isFinalBlock = read == 0;
            if (read == 0 && leftover == 0) break;

            // A partially-consumed value must restart from a state that matches the
            // compacted buffer; JsonReaderState already encodes that (it is position
            // independent), so nothing else to fix up here.
        }

        return index;
    }

    /// <summary>索引模式:按字节范围 seek + 解析单个材质条目(Clone 脱离临时文档,调用方持有即安全)。</summary>
    private JsonElement? LoadIndexedEntry(string normalizedKey)
    {
        if (_index == null || _indexedPath == null) return null;
        if (!_index.TryGetValue(normalizedKey, out MaterialEntryRange range)) return null;

        byte[] bytes = new byte[range.Length];
        using (FileStream stream = File.OpenRead(_indexedPath))
        {
            stream.Seek(range.Start, SeekOrigin.Begin);
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < bytes.Length) return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public JsonElement? TryGetUniformExpressionSet(string materialPath, string? shaderPlatform = null)
    {
        if (!HasSource)
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
        foreach (JsonElement materialEntry in EnumerateMaterialEntries())
        {
            if (materialEntry.ValueKind != JsonValueKind.Object) continue;
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
    /// <summary>
    /// 逐材质条目枚举(两种模式统一):内存模式直接给字典值;索引模式按索引键逐个 seek+解析,
    /// 峰值内存 = 单个最大材质条目(条目 Clone 自带独立文档,消费方持有引用即安全)。
    /// </summary>
    private IEnumerable<JsonElement> EnumerateMaterialEntries()
    {
        if (_materialInterfaces != null)
        {
            foreach (KeyValuePair<string, JsonElement> kvp in _materialInterfaces) yield return kvp.Value;
            yield break;
        }
        if (_index == null) yield break;
        foreach (string key in _index.Keys)
        {
            JsonElement? entry = LoadIndexedEntry(key);
            if (entry.HasValue) yield return entry.Value;
        }
    }

    public JsonElement? TryGetRenderState(string materialPath)
    {
        if (!HasSource)
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
        if (!HasSource)
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
                SerializedProgramData built = MaterialSymbolMetadataBuilder.Build(inputs);
                // 材质贴图的**声明序名表**:UE 生成 HLSL 时按 UniformTextureParameters 的桶序 + 桶内序
                // 声明 `Material.Texture2D_<i>` 等,寄存器随声明序分配 —— 所以这张扁平表的第 k 项就是
                // 本 shader 第 k 个材质贴图槽。之前这里只建 cbuffer、完全不产 TextureParameters,
                // 材质贴图因此永远无名(Pass200 只能靠引擎 UB 种子瞎猜,还猜出重名)。
                foreach (string textureName in MaterialTextureOrder.Extract(uniformExpressionSet.Value))
                {
                    built.TextureParameters.Add(new TextureParameter
                    {
                        Name = textureName,
                        NameIndex = -1,
                        Index = built.TextureParameters.Count,   // 序数;真实 t 槽由 Pass200 按声明序对位
                        SamplerIndex = -1,
                        MultiSampled = false,
                        Dim = 2,
                    });
                }
                MaterialSymbolSource source = new(
                    normalizedPath,
                    built,
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

        if (_materialInterfaces != null)
        {
            foreach (string candidate in EnumerateLookupKeys(materialPath))
            {
                if (_materialInterfaces.TryGetValue(NormalizeKey(candidate), out entry))
                {
                    return true;
                }
            }
            return false;
        }

        if (_index != null)
        {
            foreach (string candidate in EnumerateLookupKeys(materialPath))
            {
                JsonElement? loaded = LoadIndexedEntry(NormalizeKey(candidate));
                if (loaded.HasValue)
                {
                    entry = loaded.Value;
                    return true;
                }
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
