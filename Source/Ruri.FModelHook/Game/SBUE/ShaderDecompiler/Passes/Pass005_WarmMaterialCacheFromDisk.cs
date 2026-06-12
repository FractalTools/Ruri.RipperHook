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

        int materials = SeedMaterials(state, cached);
        int niagara = SeedNiagara(state, cached);

        state.Log($"    Warm cache: seeded {materials} material(s) + {niagara} Niagara hash bridge(s) from prior run in {sw.ElapsedMilliseconds} ms"
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
                case nameof(UnifiedShaderMetadataRoot.MaterialInterfaces):
                    root.MaterialInterfaces = serializer.Deserialize<Dictionary<string, UnifiedMaterialMetadata>>(reader) ?? new();
                    break;
                case nameof(UnifiedShaderMetadataRoot.NiagaraShaderMapHashes):
                    root.NiagaraShaderMapHashes = serializer.Deserialize<Dictionary<string, List<string>>>(reader) ?? new();
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
