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

internal static class Pass035_ExtractNiagaraShaderMapBridge
{
    public static void DoPass(ExportPipelineState state)
    {
        if (state.NiagaraBridgeExtracted) return;

        AbstractVfsFileProvider? provider = state.Provider;
        if (provider == null) return;

        var candidates = provider.Files.Values.Where(IsNiagaraCandidate).ToList();
        if (candidates.Count == 0)
        {
            state.NiagaraBridgeExtracted = true;
            state.Log("    Niagara bridge: candidates=0 (no NS_/NE_/NSC_/NM_ packages in provider).");
            return;
        }

        var bridge = state.Root.NiagaraShaderMapHashes;
        long considered = 0;
        long loaded = 0;
        long withScripts = 0;
        long hashesAdded = 0;
        long loadFailures = 0;
        long processed = 0;

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

        foreach (var partial in perThread.Values)
        {
            foreach (var (hash, asset) in partial)
            {
                AddBridge(bridge, hash, asset);
            }
        }

        foreach (string msg in firstFailures.Take(5))
        {
            HookLogger.LogWarning($"[Pass035_ExtractNiagaraShaderMapBridge] {msg}");
        }

        state.NiagaraBridgeExtracted = true;
        state.Root.NiagaraBridgeComplete = true;
        state.Log($"    Niagara bridge: candidates={considered}, loaded={loaded}, with-scripts={withScripts}, hashes-added={hashesAdded}, total-bridge-keys={bridge.Count}, skipped-on-error={loadFailures}, took {sw.Elapsed.TotalSeconds:F1}s.");
    }

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
