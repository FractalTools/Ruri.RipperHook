using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Niagara;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 035 — Build the Niagara-side hash bridge from
// `FNiagaraShaderMap.ResourceHash` (the FSHAHash that registers the
// shader-map with `FShaderCodeLibrary` at cook time and matches the
// `.ushaderbytecode` archive's `ShaderMapHashes` array) back to the
// owning Niagara asset path.
//
// This is the THIRD bridge in the unified metadata graph; the first two
// are already handled by Pass 020 (IoStore container `PackageShaderMapHashes`)
// and Pass 030 (per-material `LoadedShaderMaps[*].CookedShaderMapIdHash`).
// Niagara needs its own pass because:
//
//   1. The IoStore container header's per-package `ShaderMapHashes` lists
//      ONLY the FMaterialShaderMapId hashes that the cook pipeline
//      registered for material packages. UNiagaraScript / UNiagaraSystem
//      packages are not in that list at all (or are listed without the
//      Niagara shader-map hashes — depends on the cook target).
//
//   2. The inline `LoadedShaderMaps[*].CookedShaderMapIdHash` path is
//      material-specific (`FMaterialShaderMap.ShaderMapId`); Niagara has
//      its own `FNiagaraShaderMapId` with a completely different hash
//      derivation (CompilerVersion + DataInterface type set + script
//      hash + permutation flags). Hashes from one ID space NEVER match
//      hashes from the other, so the material bridges always miss.
//
//   3. The hash that DOES match the .ushaderbytecode archive for a
//      Niagara shader-map is `FShaderMapBase.ResourceHash`, set during
//      `FShaderMapBase.Deserialize` when `bShareCode == true` (the
//      default for shipping cooks). This is the same field UE's
//      `FShaderCodeLibrary::AddShaderCode` uses as the registration key
//      regardless of whether the shader came from a material or a
//      Niagara script.
//
// Concretely: this pass is what makes archives like `X6Game_10_2537`
// (101/101 UnknownMaterial without it — confirmed via earlier diagnosis)
// resolve to actual Niagara asset names. Without it, those shader-map
// hashes appear ONLY in the Niagara archive and nowhere else, so every
// downstream lookup fails.
//
// Cost model: the pass walks Niagara-prefixed packages directly from the
// provider (instead of e.g. iterating `state.Root.MaterialInterfaces`
// values, which would only cover packages Pass 030 successfully loaded).
// CUE4Parse's package cache means each `provider.LoadPackage` is cheap
// after Pass 030 already touched the file. Empty-result packages cost
// one cache hit each.
internal static class Pass035_ExtractNiagaraShaderMapBridge
{
    public static void DoPass(ExportPipelineState state)
    {
        // Whole-provider scan — runs once per FModel session. The provider's
        // mounted VFS set is fixed for the session, so the resulting bridge
        // is invariant across consecutive `ExportData_Hook` fires. The gate
        // is the cheap correctness fix for the user-visible cost: walking
        // every Niagara package can take a few seconds on a big game and
        // we don't want it re-running per shader-archive export.
        if (state.NiagaraBridgeExtracted) return;

        AbstractVfsFileProvider? provider = state.Provider;
        if (provider == null) return;

        // Pre-filter to the candidate list so we know the upfront work
        // size — used to drive progress logging and to size the
        // parallel partition.
        var candidates = provider.Files.Values.Where(IsNiagaraCandidate).ToList();
        if (candidates.Count == 0)
        {
            state.NiagaraBridgeExtracted = true;
            state.Log("    Niagara bridge: candidates=0 (no NS_/NE_/NSC_/NM_ packages in provider).");
            return;
        }

        // Per-thread accumulators avoid lock contention on the bridge
        // dictionary. Each worker builds a private (hash -> assets) map
        // and we merge them sequentially at the end. With 5000+ Niagara
        // packages and 4-8 worker threads, the merge is microseconds vs.
        // a contended dict that would serialize every Add.
        var bridge = state.Root.NiagaraShaderMapHashes;
        long considered = 0;
        long loaded = 0;
        long withScripts = 0;
        long hashesAdded = 0;
        long loadFailures = 0;
        long processed = 0;

        // Cap parallelism — Niagara packages are AES-encrypted on disk,
        // and the LoadPackage path goes through AES decrypt + zstd/oodle
        // decompress + full deserialize. Disk IO + crypto saturate around
        // 4 cores; more threads just thrash. Keep the cap modest.
        int parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));

        state.Log($"    Niagara bridge: starting walk over {candidates.Count} candidate packages ({parallelism}-way parallel)...");
        var sw = Stopwatch.StartNew();
        long lastProgressTick = 0;
        object progressLock = new();

        var perThread = new ThreadLocal<List<(string Hash, string Asset)>>(() => new List<(string, string)>(64), trackAllValues: true);
        var firstFailures = new ConcurrentBag<string>();

        Parallel.ForEach(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            (file, _) =>
            {
                Interlocked.Increment(ref considered);
                string packagePath = file.PathWithoutExtension;

                IPackage? package = null;
                try
                {
                    package = provider.LoadPackage(packagePath);
                    Interlocked.Increment(ref loaded);
                }
                catch (Exception ex)
                {
                    long fc = Interlocked.Increment(ref loadFailures);
                    if (fc <= 5)
                    {
                        firstFailures.Add($"{packagePath}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (package != null)
                {
                    int packageHashCount = 0;
                    var local = perThread.Value!;
                    foreach (UObject export in package.GetExports())
                    {
                        // CUE4Parse only types `UNiagaraScript` / `UNiagaraScriptBase`;
                        // a UNiagaraSystem package typically embeds the per-stage
                        // scripts as separate exports in the same package, so a
                        // single `LoadPackage` walk covers Spawn / Update / Render /
                        // GPU compute scripts in one go.
                        if (export is not UNiagaraScript script) continue;
                        FNiagaraShaderScript[]? resources = script.LoadedScriptResources;
                        if (resources == null) continue;

                        foreach (FNiagaraShaderScript shaderScript in resources)
                        {
                            var map = shaderScript?.RenderingThreadShaderMap;
                            var hashObj = map?.ResourceHash;
                            if (hashObj == null) continue;
                            string hash = hashObj.ToString();
                            if (string.IsNullOrWhiteSpace(hash)) continue;
                            local.Add((hash, packagePath));
                            packageHashCount++;
                        }
                    }
                    if (packageHashCount > 0)
                    {
                        Interlocked.Increment(ref withScripts);
                        Interlocked.Add(ref hashesAdded, packageHashCount);
                    }
                }

                // Progress nudge every ~500 packages OR every 5 seconds,
                // whichever comes first. Fires from a worker thread; Log()
                // is thread-safe via HookLogger's underlying console writer.
                long pc = Interlocked.Increment(ref processed);
                if (pc % 500 == 0 || sw.ElapsedMilliseconds - Interlocked.Read(ref lastProgressTick) > 5000)
                {
                    lock (progressLock)
                    {
                        if (sw.ElapsedMilliseconds - lastProgressTick > 1000)
                        {
                            lastProgressTick = sw.ElapsedMilliseconds;
                            state.Log($"    Niagara bridge: {pc}/{candidates.Count} packages, {Interlocked.Read(ref hashesAdded)} hashes so far ({sw.Elapsed.TotalSeconds:F1}s).");
                        }
                    }
                }
            });

        // Merge thread-local results into the shared bridge dict. Keep
        // (hash -> assets) deduped via the same AddBridge helper.
        foreach (var partial in perThread.Values)
        {
            foreach (var (hash, asset) in partial)
            {
                AddBridge(bridge, hash, asset);
            }
        }

        // Surface up to 5 unique load-failure messages so the user sees
        // exactly what's failing (e.g. wrong AES key for a specific
        // plugin chunk) without spamming on a 5000-package walk.
        foreach (string msg in firstFailures.Take(5))
        {
            HookLogger.LogWarning($"[Pass035_ExtractNiagaraShaderMapBridge] {msg}");
        }

        state.NiagaraBridgeExtracted = true;
        // Persisted completion marker so the warm-cache pass can trust this
        // bridge on the next run and skip the whole-provider re-walk.
        state.Root.NiagaraBridgeComplete = true;
        state.Log($"    Niagara bridge: candidates={considered}, loaded={loaded}, with-scripts={withScripts}, hashes-added={hashesAdded}, total-bridge-keys={bridge.Count}, skipped-on-error={loadFailures}, took {sw.Elapsed.TotalSeconds:F1}s.");
    }

    // Hard-accept on the well-known Niagara filename prefixes (case-
    // insensitive — some games use lowercase `Ns_` / `Ne_`). We don't
    // probe paths beyond the prefix because Niagara assets generally
    // follow the `NS_`/`NE_`/`NSC_` naming convention and a full path
    // probe would pull in unrelated UAssets that LoadPackage would then
    // bail on (cheap but noisier).
    //
    // The filter is intentionally tighter than Pass 030's IsMaterialCandidate
    // because Pass 035 needs to LoadPackage (not just LoadPackageObject)
    // to walk all exports — the broader filter would multiply the work
    // for no benefit, since non-Niagara packages can't carry
    // FNiagaraShaderScript.
    private static bool IsNiagaraCandidate(CUE4Parse.FileProvider.Objects.GameFile file)
    {
        if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return false;
        string name = file.Name;
        return name.StartsWith("NS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NE_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSCS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NM_", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddBridge(Dictionary<string, List<string>> bridge, string hash, string packagePath)
    {
        if (!bridge.TryGetValue(hash, out List<string>? assets))
        {
            assets = new List<string>();
            bridge[hash] = assets;
        }
        if (!assets.Contains(packagePath, StringComparer.OrdinalIgnoreCase))
        {
            assets.Add(packagePath);
        }
    }
}
