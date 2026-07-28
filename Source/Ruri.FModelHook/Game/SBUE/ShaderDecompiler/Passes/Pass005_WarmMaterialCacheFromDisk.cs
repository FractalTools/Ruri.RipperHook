using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 005 — Black-hole material-symbol cache (warm side).
//
// The export side's two heavy "符号拉取" (symbol-pull) passes both call
// `provider.LoadPackage` per asset:
//   * Pass 030 (material scan)  — ~40s for 366 materials; minutes for the master.
//   * Pass 035 (Niagara bridge) — a whole-provider walk of 13k+ packages (~3min).
// Within ONE session the in-memory caches make these run once. ACROSS sessions
// (each CLI invocation is a fresh process) the work was fully repeated — which
// is the user-reported "材质球符号拉取一次之后就不要重复拉取了 不然每次导出都很慢".
//
// The export pipeline already PERSISTS every scanned material + the full
// Niagara bridge into `<ProjectOutputRoot>/UnifiedShaderMetadata.json`
// (Pass 080). This pass closes the loop: at the very start of a session it
// reloads that file and seeds the in-memory caches, so already-pulled symbols
// are NEVER pulled again.
//
// Validity: the cache is keyed on the captured `GameVersionEnum`. A different
// game / engine fork (different EGame) ignores the seed and does a full
// re-scan. Within one game version, package contents are stable, so reuse is
// safe. Deleting `UnifiedShaderMetadata.json` forces a cold rebuild.
//
//   * Materials are seeded entry-by-entry into `LoadedMaterialCache` AND
//     `Root.MaterialInterfaces`. This is safe even from a PARTIAL prior run:
//     each material is independently valid, and Pass 030 still loads any
//     package the cache is missing (cache miss -> LoadPackage).
//   * The Niagara bridge is WHOLE-PROVIDER / all-or-nothing, so it is only
//     trusted (and Pass 035 skipped) when the prior run stamped
//     `NiagaraBridgeComplete = true`.
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

        // Cache-format guard. A file written by an older tool build may be
        // missing fields the current extraction produces (e.g. the per-shader-map
        // ResourceHash bridge) — seeding from it would permanently serve
        // incomplete symbols. Re-scan cold instead.
        if (cached.CacheFormatVersion != UnifiedShaderMetadataRoot.CurrentCacheFormatVersion)
        {
            state.Log($"    Warm cache: format version {cached.CacheFormatVersion} != current {UnifiedShaderMetadataRoot.CurrentCacheFormatVersion} — ignoring stale cache, doing a full re-scan.");
            return;
        }

        // Game-version guard. A mismatch means the persisted symbols belong to
        // a different cook — don't trust them.
        string currentGame = state.Provider?.Versions?.Game.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(cached.GameVersionEnum)
            && !string.IsNullOrEmpty(currentGame)
            && !string.Equals(cached.GameVersionEnum, currentGame, StringComparison.OrdinalIgnoreCase))
        {
            state.Log($"    Warm cache: persisted GameVersionEnum '{cached.GameVersionEnum}' != current '{currentGame}' — ignoring stale cache, doing a full re-scan.");
            return;
        }

        // 强制重拉材质符号的逃生口:`RURI_FORCE_MATERIAL_SYMBOLS=1`。
        //
        // 为什么需要:种缓存种的是**已经构建好的符号**(SerializedProgramData),种上之后
        // `MaterialConstantBufferReader` 对这些材质**一次都不会跑**。于是任何"从 UES 现算"的
        // 新产出(比如 preshader 的**数值求值**结果)在暖启动下永远是空的 —— 而且失败是静默的:
        // 导出照常成功,只是新头块不出现,看日志根本看不出来(实测白跑一小时才发现)。
        // 凡是给 cbuffer 读取器加新产出,验证时都要走这条路,否则测的是缓存不是新代码。
        bool forceSymbols = Environment.GetEnvironmentVariable("RURI_FORCE_MATERIAL_SYMBOLS") == "1";
        if (forceSymbols)
        {
            state.Log("    Warm cache: RURI_FORCE_MATERIAL_SYMBOLS=1 — 不种材质符号,逼 MaterialConstantBufferReader 从 UES 现算。");
        }
        int materials = forceSymbols ? 0 : SeedMaterials(state, cached);
        int bridge = SeedResourceHashBridge(state, cached);
        int niagara = SeedNiagara(state, cached);

        // Trust the prior TIER-1 material bridge as exhaustive ONLY when it
        // stamped the completion marker AND actually carries bridge entries —
        // same all-or-nothing contract as the Niagara bridge. A pre-marker
        // (older tool) or empty bridge leaves the flag false, so Pass 030
        // re-builds the full hash->material bridge and re-stamps it.
        // 强制重建逃生口:`RURI_FORCE_MATERIAL_BRIDGE=1`。
        //
        // 为什么需要:`MaterialScanComplete` 是**持久化**的"我已经扫全了"标记,一旦某次运行在
        // 覆盖不全的情况下把它盖上,后面每一次运行都会跳过 Pass 030 的桥重建,缺的材质**永远**
        // 补不回来 —— 而且失败是静默的:桥断了只表现为"某些 shader map 认不出自己属于哪个材质",
        // 于是它们的 cbuffer 符号沿用别的材质的名字,材质图算出来是错的但不报错。
        // (实测 S0165 一套 15 个材质只有 2 个在桥里,其余 13 个内核背着别家的参数名。)
        if (Environment.GetEnvironmentVariable("RURI_FORCE_MATERIAL_BRIDGE") == "1")
        {
            state.Log("    Warm cache: RURI_FORCE_MATERIAL_BRIDGE=1 — 忽略持久化的 MaterialScanComplete,强制重建 hash->material 桥。");
        }
        else if (cached.MaterialScanComplete && bridge > 0)
        {
            state.MaterialScanComplete = true;
            // Re-stamp the completion marker on the live Root so Pass 080
            // PERSISTS it on this run's rewrite — otherwise a warm run would
            // write MaterialScanComplete=false and the NEXT run would re-build
            // the whole bridge (the same all-or-nothing re-stamp SeedNiagara
            // does for NiagaraBridgeComplete).
            state.Root.MaterialScanComplete = true;
        }

        state.Log($"    Warm cache: seeded {materials} material(s) + {bridge} hash->material bridge entr(ies) + {niagara} Niagara hash bridge(s) from prior run in {sw.ElapsedMilliseconds} ms"
                  + $"{(state.MaterialScanComplete ? " (Pass 030 bridge build will be SKIPPED)" : "")}"
                  + $"{(state.NiagaraBridgeExtracted ? " (Pass 035 walk will be SKIPPED)" : "")}."
                  + " Already-pulled symbols will not be re-pulled.");
    }

    // Stream-read ONLY the cache-relevant top-level properties, skipping the
    // heavy `PackageShaderMapHashes` (Pass 020 re-derives it from the container
    // header in ~200ms) and `ShaderCodeArchives` (per-archive shader binding
    // detail the cache never reads). On the master cook the unified file is
    // 100MB+; materialising those two sections just to throw them away cost
    // most of the warm-start deserialize. JsonReader.Skip() walks past them
    // without allocating the object graph.
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
            if (!reader.Read()) break; // advance onto the property value

            switch (prop)
            {
                case nameof(UnifiedShaderMetadataRoot.CacheFormatVersion):
                    root.CacheFormatVersion = reader.TokenType == JsonToken.Integer ? Convert.ToInt32(reader.Value) : 0;
                    // Short-circuit a stale cache BEFORE deserializing the
                    // (potentially multi-GB) MaterialInterfaces. CacheFormatVersion
                    // is serialised first, so a mismatch is detectable from a few
                    // bytes — no point materialising the whole document just to
                    // discard it. DoPass re-checks and logs the skip.
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
                    reader.Skip(); // PackageShaderMapHashes / ShaderCodeArchives — not needed by the cache
                    break;
            }
        }
        return root;
    }

    // Seed the per-package material cache. Both `LoadedMaterialCache` (so
    // Pass 030 short-circuits LoadPackage) and `Root.MaterialInterfaces` (so
    // the cumulative output carries prior materials forward) are populated.
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

    // Seed the TIER-1 (hash -> material paths) bridge. Top-level and small
    // (~tens of thousands of hash keys), so it always loads regardless of the
    // unified file's size — this is what keeps material NAMING working even
    // when the heavy MaterialInterfaces block is skipped on a multi-GB cook.
    private static int SeedResourceHashBridge(ExportPipelineState state, UnifiedShaderMetadataRoot cached)
    {
        if (cached.MaterialResourceHashes == null || cached.MaterialResourceHashes.Count == 0) return 0;
        foreach (KeyValuePair<string, List<string>> kv in cached.MaterialResourceHashes)
        {
            if (kv.Value != null) state.Root.MaterialResourceHashes[kv.Key] = kv.Value;
        }
        return state.Root.MaterialResourceHashes.Count;
    }

    // Seed the Niagara bridge. Only trusted when the prior run stamped the
    // completion marker — a partial Niagara walk must not be cemented as the
    // whole answer.
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
        state.NiagaraBridgeExtracted = true;   // skip the whole-provider re-walk in Pass 035
        return cached.NiagaraShaderMapHashes.Count;
    }
}
