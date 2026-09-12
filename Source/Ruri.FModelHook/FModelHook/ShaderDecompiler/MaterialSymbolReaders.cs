using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ruri.Hook.Core;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

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

        JsonElement materialAsset = SelectMaterialAsset(root);
        SymbolInputs? inputs = SymbolInputsReader.Read(normalizedPath, shaderPlatform, materialAsset);
        if (inputs == null)
        {
            _cache[cacheKey] = null;
            return null;
        }

        MaterialParameterCollectionReader.ResolveAndInject(materialAsset, inputs, _exportRoot, _exportRootName);

        MaterialSymbolSource source = BuildSource(normalizedPath, inputs);
        _cache[cacheKey] = source;
        return source;
    }

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

    private bool HasSource => _materialInterfaces != null || _index != null;

    private const long MaxInMemoryUnifiedBytes = 1024L * 1024 * 1024;
    public static UnifiedMaterialReader? LoadFromFile(string unifiedMetadataPath)
    {
        if (string.IsNullOrWhiteSpace(unifiedMetadataPath) || !File.Exists(unifiedMetadataPath))
        {
            return null;
        }

        long length = new FileInfo(unifiedMetadataPath).Length;
        if (length > MaxInMemoryUnifiedBytes)
        {
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

    private static Dictionary<string, MaterialEntryRange>? BuildMaterialInterfaceIndex(string path)
    {
        var index = new Dictionary<string, MaterialEntryRange>(StringComparer.OrdinalIgnoreCase);
        using FileStream stream = File.OpenRead(path);

        byte[] buffer = new byte[1 << 20];
        int dataLength = stream.Read(buffer, 0, buffer.Length);
        bool isFinalBlock = dataLength < buffer.Length;
        long bufferStartOffset = 0;        JsonReaderState state = new(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        bool insideMaterialInterfaces = false;
        int materialInterfacesInnerDepth = -1;
        bool pendingMaterialInterfaces = false;        string? pendingMaterialName = null;
        bool done = false;

        while (!done)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, dataLength), isFinalBlock, state);
            bool needMoreData = false;
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
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            int read = stream.Read(buffer, leftover, buffer.Length - leftover);
            dataLength = leftover + read;
            isFinalBlock = read == 0;
            if (read == 0 && leftover == 0) break;

        }

        return index;
    }

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

    public JsonElement? TryGetUniformExpressionSet(string materialPath, string? shaderPlatform = null, string? shaderMapHash = null)
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

        return SelectUniformExpressionSet(materialEntry, shaderPlatform, shaderMapHash);
    }

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

    public MaterialSymbolSource? GetSource(string materialPath, string? shaderPlatform = null, string? shaderMapHash = null)
    {
        if (!HasSource)
        {
            return null;
        }

        string normalizedPath = materialPath.Replace('\\', '/');
        string cacheKey = string.IsNullOrWhiteSpace(shaderPlatform)
            ? normalizedPath
            : normalizedPath + "|" + shaderPlatform;
        if (!string.IsNullOrWhiteSpace(shaderMapHash))
        {
            cacheKey += "|" + shaderMapHash;
        }

        if (_cache.TryGetValue(cacheKey, out MaterialSymbolSource? cached))
        {
            return cached;
        }

        if (!TryResolveMaterialEntry(normalizedPath, out JsonElement materialEntry))
        {
            _cache[cacheKey] = null;
            return null;
        }

        JsonElement? uniformExpressionSet = SelectUniformExpressionSet(materialEntry, shaderPlatform, shaderMapHash);
        if (uniformExpressionSet.HasValue)
        {
            SymbolInputs? inputs = SymbolInputsReader.ReadFromUniformExpressionSet(normalizedPath, shaderPlatform, uniformExpressionSet.Value);
            if (inputs != null)
            {
                SerializedProgramData built = MaterialSymbolMetadataBuilder.Build(inputs);
                foreach (string textureName in MaterialTextureOrder.Extract(uniformExpressionSet.Value))
                {
                    built.TextureParameters.Add(new TextureParameter
                    {
                        Name = textureName,
                        NameIndex = -1,
                        Index = built.TextureParameters.Count,                        SamplerIndex = -1,
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

        List<string> textureNames = new();
        AppendStringArray(cachedParams, "TextureNames", textureNames);
        AppendStringArray(cachedParams, "RuntimeVirtualTextureNames", textureNames);
        AppendStringArray(cachedParams, "SparseVolumeTextureNames", textureNames);
        AppendStringArray(cachedParams, "FontNames", textureNames);

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

        if (metadata.TextureParameters.Count == 0)
        {
            return null;
        }

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

    private static JsonElement? SelectUniformExpressionSet(JsonElement materialEntry, string? preferredShaderPlatform, string? targetShaderMapHash = null)
    {
        if (!materialEntry.TryGetProperty("LoadedShaderMaps", out JsonElement loadedShaderMaps) || loadedShaderMaps.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string?> packageHashes = new();
        if (materialEntry.TryGetProperty("PackageShaderMapHashes", out JsonElement pkgHashes) && pkgHashes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement h in pkgHashes.EnumerateArray())
            {
                packageHashes.Add(h.ValueKind == JsonValueKind.String ? h.GetString() : null);
            }
        }

        JsonElement? fallback = null;
        JsonElement? bestMatch = null;
        int bestMatchCount = -1;
        int fallbackCount = -1;
        int mapIndex = -1;
        foreach (JsonElement shaderMap in loadedShaderMaps.EnumerateArray())
        {
            mapIndex++;
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

            if (!string.IsNullOrWhiteSpace(targetShaderMapHash))
            {
                string? mapHash = ReadString(shaderMap, "ResourceHash");
                if (string.IsNullOrWhiteSpace(mapHash))
                {
                    mapHash = mapIndex < packageHashes.Count ? packageHashes[mapIndex] : null;
                }

                if (string.IsNullOrWhiteSpace(mapHash))
                {
                    mapHash = ReadString(shaderMap, "CookedShaderMapIdHash") ?? ReadString(shaderMap, "ShaderContentHash");
                }

                if (!string.IsNullOrWhiteSpace(mapHash)
                    && mapHash.StartsWith(targetShaderMapHash, StringComparison.OrdinalIgnoreCase))
                {
                    HashMatchedSelections++;
                    return ues.Clone();
                }
            }

            string? shaderPlatform = ReadString(shaderMap, "ShaderPlatform");
            bool platformMatches = !string.IsNullOrWhiteSpace(preferredShaderPlatform)
                && string.Equals(shaderPlatform, preferredShaderPlatform, StringComparison.OrdinalIgnoreCase);

            int preshaderCount = ues.TryGetProperty("UniformPreshaders", out JsonElement preshaderArray)
                                 && preshaderArray.ValueKind == JsonValueKind.Array
                ? preshaderArray.GetArrayLength()
                : 0;

            if (platformMatches)
            {
                if (preshaderCount > bestMatchCount)
                {
                    bestMatchCount = preshaderCount;
                    bestMatch = ues.Clone();
                }

                continue;
            }

            if (preshaderCount > fallbackCount)
            {
                fallbackCount = preshaderCount;
                fallback = ues.Clone();
            }
        }

        if (bestMatch.HasValue || fallback.HasValue)
        {
            HeuristicSelections++;
        }

        return bestMatch ?? fallback;
    }

    public static int HashMatchedSelections;

    public static int HeuristicSelections;

    public IEnumerable<(string ShaderPlatform, int PreshaderBufferSize, int NumPreshaders)>
        EnumerateUniformExpressionSets(string materialPath)
    {
        if (!HasSource) yield break;
        if (!TryResolveMaterialEntry(materialPath.Replace('\\', '/'), out JsonElement materialEntry)) yield break;
        if (!materialEntry.TryGetProperty("LoadedShaderMaps", out JsonElement maps) || maps.ValueKind != JsonValueKind.Array) yield break;

        foreach (JsonElement shaderMap in maps.EnumerateArray())
        {
            if (shaderMap.ValueKind != JsonValueKind.Object) continue;
            if (!shaderMap.TryGetProperty("MaterialShaderMapContent", out JsonElement content) || content.ValueKind != JsonValueKind.Object) continue;
            if (!content.TryGetProperty("UniformExpressionSet", out JsonElement ues) || ues.ValueKind != JsonValueKind.Object) continue;

            int size = ues.TryGetProperty("UniformPreshaderBufferSize", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt32() : -1;
            int count = ues.TryGetProperty("UniformPreshaders", out JsonElement pre) && pre.ValueKind == JsonValueKind.Array ? pre.GetArrayLength() : -1;

            int cbSize = -1;
            if (ues.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement ubl) && ubl.ValueKind == JsonValueKind.Object)
            {
                if (ubl.TryGetProperty("ConstantBufferSize", out JsonElement cbs) && cbs.ValueKind == JsonValueKind.Number)
                {
                    cbSize = cbs.GetInt32();
                }

                if (ubl.TryGetProperty("Resources", out JsonElement res) && res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0
                    && res[0].TryGetProperty("MemberOffset", out JsonElement mo) && mo.ValueKind == JsonValueKind.Number)
                {
                    Console.WriteLine($"    [ues-bounds] cbSize={cbSize} resource0Offset={mo.GetInt32()} preshaderBytes={size * 16}");
                }
                else
                {
                    Console.WriteLine($"    [ues-bounds] cbSize={cbSize} resource0Offset=<无> preshaderBytes={size * 16}");
                }
            }

            yield return (ReadString(shaderMap, "ShaderPlatform") ?? "?", size, count);
        }
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
