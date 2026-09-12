using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass005_WarmMaterialCacheFromDisk
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.MaterialCacheWarmed) return;
        state.MaterialCacheWarmed = true;

        string unifiedPath = Path.Combine(state.ProjectOutputRoot ?? string.Empty, "UnifiedShaderMetadata.json");
        if (string.IsNullOrEmpty(state.ProjectOutputRoot) || !File.Exists(unifiedPath))
        {
            state.Log("    Warm cache: no prior UnifiedShaderMetadata.json — cold start (materials + Niagara will be pulled fresh).");
            return;
        }

        UnifiedShaderMetadataRoot? cached;
        var sw = Stopwatch.StartNew();
        try
        {
            cached = ReadCacheSubset(unifiedPath);
        }
        catch (Exception ex)
        {
            state.LogError($"    Warm cache: failed to read {unifiedPath}: {ex.Message} — falling back to a cold scan.");
            return;
        }
        if (cached == null) return;

        if (cached.CacheFormatVersion != UnifiedShaderMetadataRoot.CurrentCacheFormatVersion)
        {
            state.Log($"    Warm cache: format version {cached.CacheFormatVersion} != current {UnifiedShaderMetadataRoot.CurrentCacheFormatVersion} — ignoring stale cache, doing a full re-scan.");
            return;
        }

        string currentGame = state.Provider?.Versions?.Game.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(cached.GameVersionEnum)
            && !string.IsNullOrEmpty(currentGame)
            && !string.Equals(cached.GameVersionEnum, currentGame, StringComparison.OrdinalIgnoreCase))
        {
            state.Log($"    Warm cache: persisted GameVersionEnum '{cached.GameVersionEnum}' != current '{currentGame}' — ignoring stale cache, doing a full re-scan.");
            return;
        }

        bool forceSymbols = Environment.GetEnvironmentVariable("RURI_FORCE_MATERIAL_SYMBOLS") == "1";
        if (forceSymbols)
        {
            state.Log("    Warm cache: RURI_FORCE_MATERIAL_SYMBOLS=1 — 不种材质符号,逼 MaterialConstantBufferReader 从 UES 现算。");
        }
        int materials = forceSymbols ? 0 : SeedMaterials(state, cached);
        int bridge = SeedResourceHashBridge(state, cached);
        int niagara = SeedNiagara(state, cached);

        if (Environment.GetEnvironmentVariable("RURI_FORCE_MATERIAL_BRIDGE") == "1")
        {
            state.Log("    Warm cache: RURI_FORCE_MATERIAL_BRIDGE=1 — 忽略持久化的 MaterialScanComplete,强制重建 hash->material 桥。");
        }
        else if (cached.MaterialScanComplete && bridge > 0)
        {
            state.MaterialScanComplete = true;
            state.Root.MaterialScanComplete = true;
        }

        state.Log($"    Warm cache: seeded {materials} material(s) + {bridge} hash->material bridge entr(ies) + {niagara} Niagara hash bridge(s) from prior run in {sw.ElapsedMilliseconds} ms"
                  + $"{(state.MaterialScanComplete ? " (Pass 030 bridge build will be SKIPPED)" : "")}"
                  + $"{(state.NiagaraBridgeExtracted ? " (Pass 035 walk will be SKIPPED)" : "")}."
                  + " Already-pulled symbols will not be re-pulled.");
    }

    private static UnifiedShaderMetadataRoot ReadCacheSubset(string path)
    {
        var root = new UnifiedShaderMetadataRoot();
        JsonSerializer serializer = JsonSerializer.CreateDefault();

        using var stream = File.OpenRead(path);
        using var textReader = new StreamReader(stream);
        using var reader = new JsonTextReader(textReader);

        if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return root;
        while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
        {
            string prop = (string)reader.Value!;
            if (!reader.Read()) break;
            switch (prop)
            {
                case nameof(UnifiedShaderMetadataRoot.CacheFormatVersion):
                    root.CacheFormatVersion = reader.TokenType == JsonToken.Integer ? Convert.ToInt32(reader.Value) : 0;
                    if (root.CacheFormatVersion != UnifiedShaderMetadataRoot.CurrentCacheFormatVersion)
                        return root;
                    break;
                case nameof(UnifiedShaderMetadataRoot.GameVersionEnum):
                    root.GameVersionEnum = reader.Value?.ToString() ?? string.Empty;
                    break;
                case nameof(UnifiedShaderMetadataRoot.NiagaraBridgeComplete):
                    root.NiagaraBridgeComplete = reader.TokenType == JsonToken.Boolean && (bool)reader.Value!;
                    break;
                case nameof(UnifiedShaderMetadataRoot.MaterialScanComplete):
                    root.MaterialScanComplete = reader.TokenType == JsonToken.Boolean && (bool)reader.Value!;
                    break;
                case nameof(UnifiedShaderMetadataRoot.MaterialInterfaces):
                    root.MaterialInterfaces = serializer.Deserialize<Dictionary<string, UnifiedMaterialMetadata>>(reader) ?? new();
                    break;
                case nameof(UnifiedShaderMetadataRoot.NiagaraShaderMapHashes):
                    root.NiagaraShaderMapHashes = serializer.Deserialize<Dictionary<string, List<string>>>(reader) ?? new();
                    break;
                case nameof(UnifiedShaderMetadataRoot.MaterialResourceHashes):
                    root.MaterialResourceHashes = serializer.Deserialize<Dictionary<string, List<string>>>(reader) ?? new();
                    break;
                default:
                    reader.Skip();                    break;
            }
        }
        return root;
    }

    private static int SeedMaterials(ExportPipelineState state, UnifiedShaderMetadataRoot cached)
    {
        if (cached.MaterialInterfaces == null || cached.MaterialInterfaces.Count == 0) return 0;

        int count = 0;
        foreach (KeyValuePair<string, UnifiedMaterialMetadata> kv in cached.MaterialInterfaces)
        {
            if (kv.Value == null) continue;
            state.LoadedMaterialCache[kv.Key] = kv.Value;
            state.Root.MaterialInterfaces[kv.Key] = kv.Value;
            count++;
        }
        return count;
    }

    private static int SeedResourceHashBridge(ExportPipelineState state, UnifiedShaderMetadataRoot cached)
    {
        if (cached.MaterialResourceHashes == null || cached.MaterialResourceHashes.Count == 0) return 0;
        foreach (KeyValuePair<string, List<string>> kv in cached.MaterialResourceHashes)
        {
            if (kv.Value != null) state.Root.MaterialResourceHashes[kv.Key] = kv.Value;
        }
        return state.Root.MaterialResourceHashes.Count;
    }

    private static int SeedNiagara(ExportPipelineState state, UnifiedShaderMetadataRoot cached)
    {
        if (cached.NiagaraShaderMapHashes == null || cached.NiagaraShaderMapHashes.Count == 0) return 0;
        if (!cached.NiagaraBridgeComplete)
        {
            state.Log("    Warm cache: prior Niagara bridge is incomplete (no completion marker) — Pass 035 will re-walk.");
            return 0;
        }

        foreach (KeyValuePair<string, List<string>> kv in cached.NiagaraShaderMapHashes)
        {
            if (kv.Value != null) state.Root.NiagaraShaderMapHashes[kv.Key] = kv.Value;
        }
        state.Root.NiagaraBridgeComplete = true;
        state.NiagaraBridgeExtracted = true;        return cached.NiagaraShaderMapHashes.Count;
    }
}
