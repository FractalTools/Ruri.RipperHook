using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Ruri.Hook.Core;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.RenderCore;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 030 — Walk every material UAsset known to FModel's provider and
// fold its `LoadedMaterialResources[*].LoadedShaderMap` graph (when
// inline shader-maps survived) plus `CachedExpressionData` parameter
// names + the IoStore container's `PackageShaderMapHashes` mirror into
// `state.Root.MaterialInterfaces`.
//
// Runs AFTER Pass 020 so we can scope the scan to packages whose
// shader-map hashes intersect the current archive (the IoStore hash
// index Pass 020 builds turns a multi-minute full-provider walk into a
// few-second targeted load).
//
// This is the expensive step: each candidate UAsset is loaded via
// `provider.LoadPackageObject`, parsed, and unrolled into the unified
// metadata DTO graph (FShader -> FShaderMapPointerTable -> FFrozenArchive
// -> FMaterialShaderMapContent). The result is cached on the shared
// `ExportPipelineState` so subsequent library exports in the same FModel
// session reuse the work.
//
// Why this lives in a single pass file: every helper here is consumed by
// exactly one method (`ExtractMaterialContext`); they are inlined per the
// "no helpers outside passes" rule. Splitting them into per-DTO files
// would create a dense one-way using-cycle without any reuse benefit.
internal static class Pass030_ScanMaterialPackages
{
    public static void DoPass(ExportPipelineState state)
    {
        AbstractVfsFileProvider? provider = state.Provider;
        if (provider == null) return;

        BuildMaterialContexts(state, provider);
    }

    // Two-tier material resolution, designed around the fact that the
    // InfinityNikki/X6Game IoStore container header (`PackageShaderMapHashes`)
    // associates only a tiny, unreliable fraction of shader-maps to a package
    // — so a hash-scoped scan keyed on it leaves 18-85% of every archive's
    // shader-maps as `UnknownMaterial` (the user-reported "很多 shader 找不到
    // 材质球").
    //
    //   TIER 1 — the COMPLETE shader-map-hash -> material bridge, built ONCE
    //     and cached forever. Walks every shader-map-OWNING package (the
    //     container header's StoreEntries with a non-empty hash list — the
    //     `PackageShaderMapHashes` keys, ~23k packages, NOT the 157k "any path
    //     with /Material/" set) and reads each material's authoritative inline
    //     `LoadedShaderMaps[*].ResourceHash` — the FShaderMapResource library
    //     key that IS the archive's `ShaderMapHashes`, present for every cooked
    //     material regardless of the container-header gap (the headless mount
    //     sets `provider.ReadShaderMaps = true`). Lightweight: only the hashes
    //     are kept, the package is dropped — so memory stays bounded even over
    //     23k loads. Persisted to the top-level `MaterialResourceHashes` dict
    //     and gated by MaterialScanComplete so it never re-runs while the cache
    //     is valid — the "材质球符号拉一次就不再拉" guarantee, mirroring Pass 035.
    //
    //   TIER 2 — the RICH per-material symbols (UniformExpressionSet / render
    //     state) the .shader `Properties` block needs, extracted for JUST the
    //     materials the CURRENT archive references (resolved through the Tier 1
    //     bridge). A handful of full LoadPackage calls per archive instead of
    //     the whole game, so CB-symbol extraction stays cheap and the unified
    //     file's heavy `MaterialInterfaces` block stays bounded to materials
    //     actually exported.
    private static void BuildMaterialContexts(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;
        var cache = state.LoadedMaterialCache;

        // Non-IoStore cooks have no container-header package->hash index, so
        // the candidate set can't be scoped to shader-map-owning packages.
        // Fall back to the legacy full provider walk (cached) — those games are
        // small enough for it, and their materials carry inline shader maps.
        if (output.PackageShaderMapHashes.Count == 0)
        {
            if (!state.MaterialScanComplete)
            {
                FullProviderScan(provider, output, cache, log);
                state.MaterialScanComplete = true;
                output.MaterialScanComplete = true;
            }
            return;
        }

        // TIER 1 — build (or reuse) the complete hash -> material bridge.
        if (!state.MaterialScanComplete)
        {
            BuildResourceHashBridge(state, provider);
            state.MaterialScanComplete = true;
            output.MaterialScanComplete = true;
        }
        else
        {
            log($"    Material bridge: SKIPPED — {output.MaterialResourceHashes.Count} cached hash->material entries reused. Symbols not re-pulled.");
        }

        // TIER 2 — rich symbols for this archive's materials only.
        EnrichCurrentArchiveMaterials(state, provider);
    }

    // TIER 1 — load every shader-map-owning package, read its material's
    // authoritative inline shader-map hashes, and fold them into the top-level
    // (hash -> material paths) bridge. Lightweight per package: the hashes are
    // copied out and the deserialized package is released, so peak memory is
    // ~the bridge dict, not 23k full material graphs.
    private static void BuildResourceHashBridge(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;

        // Candidate set = the container header's shader-map-owning packages
        // UNION every material-prefixed asset (M_/MI_/MF_/MPC_/MAT_). The
        // container-header set alone (~23k) misses materials whose StoreEntry
        // was cooked with an EMPTY shader-map-hash list (UE's "shader-map has no
        // associated assets" case) — these are common for effect / character
        // material instances (e.g. X6Game_5's whole material set), so without
        // the prefix union those in-game assets stay UnknownMaterial. The union
        // is ~136k here; loading them all is the ONE-TIME "符号拉取" the user
        // asked to pay once and cache forever (the "不要一次性导出整个 shader 库
        // 会卡死电脑" concern is about the DECOMPILE side, not this scan). The
        // scan is memory-bounded: CUE4Parse's LoadPackage does NOT cache, so the
        // deserialized packages are GC'd right after their hashes are copied out
        // — peak RAM is the (small) bridge dict plus the in-flight workers.
        var containerKeys = new HashSet<string>(output.PackageShaderMapHashes.Keys, StringComparer.OrdinalIgnoreCase);
        var packageSet = new HashSet<string>(containerKeys, StringComparer.OrdinalIgnoreCase);
        foreach (GameFile file in provider.Files.Values)
        {
            if (IsPrefixedMaterialAsset(file)) packageSet.Add(file.PathWithoutExtension);
        }
        var packages = packageSet.ToList();
        log($"    Material bridge: START — reading inline shader-map hashes from {packages.Count} package(s) ({containerKeys.Count} container-header shader-map owners + {packages.Count - containerKeys.Count} material-prefixed).");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var bridge = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
        // Container-header packages that yielded NO hashes on the parallel pass.
        // A container-header StoreEntry that LISTS shader-map hashes is EXPECTED
        // to own an inline shader map, so an empty there is suspicious — likely a
        // racy miss: CUE4Parse's UMaterial.Deserialize swallows the transient
        // concurrency exception and silently leaves LoadedShaderMap null
        // (UMaterial.cs try/catch). We re-check just those SINGLE-THREADED below
        // (no contention) to deterministically recover them. Prefix-only empties
        // are NOT retried — most are MIs that genuinely inherit a parent's map
        // and own none, so retrying all ~110k would cost minutes for nothing.
        var emptyContainerPackages = new ConcurrentBag<string>();
        long withHashes = 0, failures = 0;
        int parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));

        System.Threading.Tasks.Parallel.ForEach(
            packages,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
            path =>
            {
                List<string>? hashes = LoadMaterialShaderMapHashes(provider, path);
                if (hashes == null || hashes.Count == 0)
                {
                    if (containerKeys.Contains(path)) emptyContainerPackages.Add(path);
                    return;
                }
                System.Threading.Interlocked.Increment(ref withHashes);
                foreach (string h in hashes)
                {
                    ConcurrentDictionary<string, byte> set = bridge.GetOrAdd(h, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                    set.TryAdd(path, 0);
                }
            });

        // Deterministic single-threaded recovery over the suspicious empties.
        long recovered = 0;
        foreach (string path in emptyContainerPackages)
        {
            List<string>? hashes = LoadMaterialShaderMapHashes(provider, path);
            if (hashes == null) { System.Threading.Interlocked.Increment(ref failures); continue; }
            if (hashes.Count == 0) continue;
            System.Threading.Interlocked.Increment(ref recovered);
            foreach (string h in hashes)
            {
                ConcurrentDictionary<string, byte> set = bridge.GetOrAdd(h, static _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
                set.TryAdd(path, 0);
            }
        }

        // Cap the per-hash material list. One shader-map is commonly shared by
        // dozens of material instances (same parent, different param values), so
        // an uncapped list bloats the .shader UsedMaterials block to 40+ entries
        // and the bridge dict to many MB. Keep the first N alphabetically — a
        // stable, representative sample that still names the shader and shows the
        // sharing without drowning the output.
        const int maxMaterialsPerHash = 16;
        foreach (KeyValuePair<string, ConcurrentDictionary<string, byte>> kvp in bridge)
        {
            output.MaterialResourceHashes[kvp.Key] = kvp.Value.Keys
                .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
                .Take(maxMaterialsPerHash)
                .ToList();
        }

        log($"    Material bridge: DONE — packages={packages.Count}, with-shadermaps={withHashes + recovered} (parallel={withHashes} + single-thread-recovered={recovered}), bridge-hashes={output.MaterialResourceHashes.Count}, skipped-on-error={failures}, took {sw.Elapsed.TotalSeconds:F1}s.");
    }

    // Lightweight reader: load a package, take the FIRST UMaterialInterface
    // export, and return its inline shader-map hashes (ResourceHash — the
    // archive library key — plus CookedShaderMapIdHash for non-IoStore cooks).
    // Returns null on load failure, an empty list when the package has no
    // material / no inline shader map. The package graph is NOT retained.
    private static List<string>? LoadMaterialShaderMapHashes(AbstractVfsFileProvider provider, string packagePath)
    {
        CUE4Parse.UE4.Assets.IPackage? package;
        try
        {
            package = provider.LoadPackage(packagePath);
        }
        catch
        {
            return null;
        }
        if (package == null) return null;

        try
        {
            var result = new List<string>();
            foreach (CUE4Parse.UE4.Assets.Exports.UObject export in package.GetExports())
            {
                if (export is not UMaterialInterface material) continue;
                if (material.LoadedMaterialResources == null) break;
                foreach (var resource in material.LoadedMaterialResources)
                {
                    var shaderMap = resource.LoadedShaderMap;
                    if (shaderMap == null) continue;
                    string? resourceHash = shaderMap.ResourceHash?.ToString() ?? shaderMap.Code?.ResourceHash.ToString();
                    if (!string.IsNullOrWhiteSpace(resourceHash)) result.Add(resourceHash!);
                    string? cooked = shaderMap.ShaderMapId.CookedShaderMapIdHash?.ToString();
                    if (!string.IsNullOrWhiteSpace(cooked)) result.Add(cooked!);
                }
                break;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    // TIER 2 — resolve the materials the CURRENT archive references through the
    // complete Tier 1 bridge, then load JUST those fully so their
    // UniformExpressionSet / render state land in MaterialInterfaces for the
    // .shader Properties block. Cached across archives in the session.
    private static void EnrichCurrentArchiveMaterials(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;
        var cache = state.LoadedMaterialCache;
        HashSet<string> archiveHashes = state.CurrentArchiveShaderMapHashes;
        if (archiveHashes.Count == 0) return;

        // ONE representative material per shader-map hash. The rich symbols we
        // need (UniformExpressionSet parameter NAMES, render state) come from the
        // material's parent and are identical across the dozens of instances that
        // share a shader-map, so loading them all would be wasted IO. The .shader
        // Properties block is built from the primary asset's UES (Pass 170); the
        // full UsedMaterials list still comes from the (capped) bridge.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string hash in archiveHashes)
        {
            if (output.MaterialResourceHashes.TryGetValue(hash, out List<string>? materials) && materials.Count > 0)
            {
                paths.Add(materials[0]);
            }
        }

        // SINGLE-THREADED on purpose. The set is tiny (the few dozen materials
        // THIS archive references), and CUE4Parse's inline-shader-map
        // deserialize throws transiently under concurrency (swallowed in
        // UMaterial.Deserialize -> empty UniformExpressionSet), which would make
        // the .shader Properties block flicker run-to-run. Sequential here costs
        // ~1-2s and makes the rich CB symbols deterministic.
        long reused = 0, loaded = 0, failures = 0, produced = 0;
        foreach (string path in paths)
        {
            if (cache.ContainsKey(path)) { reused++; continue; }

            UnifiedMaterialMetadata? metadata = LoadAndExtractByPath(provider, path, out bool loadedOk, out bool failed);
            if (loadedOk) loaded++;
            if (failed) failures++;
            if (metadata != null && output.PackageShaderMapHashes.TryGetValue(path, out List<string>? hashes))
            {
                metadata.PackageShaderMapHashes = new List<string>(hashes);
            }
            cache[path] = metadata;
        }

        foreach (string path in paths)
        {
            if (cache.TryGetValue(path, out UnifiedMaterialMetadata? m) && m != null)
            {
                output.MaterialInterfaces[path] = m;
                produced++;
            }
        }

        log($"    Material enrich (archive-scoped): archive-hashes={archiveHashes.Count}, materials={paths.Count}, loaded={loaded}, reused={reused}, produced={produced}, skipped-on-error={failures}.");
    }

    private static void FullProviderScan(AbstractVfsFileProvider provider, UnifiedShaderMetadataRoot output, ConcurrentDictionary<string, UnifiedMaterialMetadata?> cache, Action<string> log)
    {
        // Pre-filter to the material-candidate list so the parallel partition
        // sizes correctly and the considered-count is exact.
        var candidates = provider.Files.Values.Where(IsMaterialCandidate).ToList();
        log($"    Material scan (full): START — {candidates.Count} material candidate(s) to load.");

        long reused = 0;
        long loaded = 0;
        long loadFailures = 0;
        long extracted = 0;

        int parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));
        System.Threading.Tasks.Parallel.ForEach(
            candidates,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
            file =>
            {
                string packagePath = file.PathWithoutExtension;
                if (cache.ContainsKey(packagePath)) { System.Threading.Interlocked.Increment(ref reused); return; }

                UnifiedMaterialMetadata? metadata = LoadAndExtractByPath(provider, packagePath, out bool loadedOk, out bool failed);
                if (loadedOk) System.Threading.Interlocked.Increment(ref loaded);
                if (failed) System.Threading.Interlocked.Increment(ref loadFailures);
                if (metadata != null)
                {
                    if (output.PackageShaderMapHashes.TryGetValue(packagePath, out List<string>? hashes))
                        metadata.PackageShaderMapHashes = new List<string>(hashes);
                    System.Threading.Interlocked.Increment(ref extracted);
                }
                cache[packagePath] = metadata;
            });

        foreach (var file in candidates)
        {
            string packagePath = file.PathWithoutExtension;
            if (cache.TryGetValue(packagePath, out UnifiedMaterialMetadata? m) && m != null)
                output.MaterialInterfaces[packagePath] = m;
        }

        log($"    Material scan (full): candidates={candidates.Count}, cache-reused={reused}, loaded={loaded}, extracted={extracted}, skipped-on-error={loadFailures}.");
    }

    // Shared loader: load the package, route to the right metadata
    // builder. Materials go through `ExtractMaterialContext` (the full
    // material-aware path with LoadedMaterialResources / CachedExpressionData /
    // RenderState). Other UObjects — primarily `UNiagaraScript` /
    // `UNiagaraSystem` / `UNiagaraEmitter` — go through
    // `ExtractGenericContext` which uses the generic UObject reader and
    // returns a metadata stub with `CachedParameters` populated from the
    // property-bag sweep. Either way, the per-package
    // `PackageShaderMapHashes` mirror is stamped on the result by the
    // caller, which is what bridges the shader-map hash back to a
    // package name (instead of "UnknownMaterial") downstream.
    //
    // Returns null on any failure (already logged through HookLogger).
    // Outcome is reported via out flags (not ref counters) so the method is
    // safe to call from the parallel scan workers — the caller does the
    // Interlocked accounting.
    private static UnifiedMaterialMetadata? LoadAndExtractByPath(AbstractVfsFileProvider provider, string packagePath, out bool loadedOk, out bool failed)
    {
        loadedOk = false;
        failed = false;
        CUE4Parse.UE4.Assets.IPackage? package;
        try
        {
            package = provider.LoadPackage(packagePath);
            loadedOk = true;
        }
        catch (Exception ex)
        {
            failed = true;
            HookLogger.LogWarning($"[Pass030_ScanMaterialPackages] Skipped {packagePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        if (package == null) return null;

        try
        {
            // Walk every export and prefer the FIRST UMaterialInterface
            // we find. For ordinary material packages this is the only
            // (or first) export and `LoadPackageObject` would have
            // worked the same; for level-package _Generated_ landscape
            // material instances (e.g. MainGrid_L2_X0_Y-1_DL0) the
            // FIRST export is a LandscapeComponent and the actual
            // LandscapeMaterialInstanceConstant lives a few exports
            // later — `LoadPackageObject` would fall through to the
            // generic-stub branch and lose ALL shader-map type info,
            // leaving every shader in those packages with empty
            // ShaderTypeHash/VertexFactoryTypeHash downstream.
            UMaterialInterface? material = null;
            CUE4Parse.UE4.Assets.Exports.UObject? firstExport = null;
            foreach (CUE4Parse.UE4.Assets.Exports.UObject export in package.GetExports())
            {
                firstExport ??= export;
                if (export is UMaterialInterface mat)
                {
                    material = mat;
                    break;
                }
            }
            if (material != null)
            {
                return ExtractMaterialContext(material, packagePath);
            }
            if (firstExport != null)
            {
                return ExtractGenericContext(firstExport, packagePath);
            }
            return null;
        }
        catch (Exception ex)
        {
            failed = true;
            HookLogger.LogWarning($"[Pass030_ScanMaterialPackages] Extract failed for {packagePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Build a stub `UnifiedMaterialMetadata` for any non-material
    // UObject. The only enrichment we get out of the asset itself is
    // the parameter-name property-bag sweep — everything material-
    // specific (LoadedShaderMaps inline blob, render-state UProperties
    // BlendMode/ShadingModel/etc., MaterialCompilationOutput
    // UniformExpressionSet) is skipped because the asset doesn't carry
    // those fields. The caller stamps `PackageShaderMapHashes` on top
    // before returning, which is what gives the file a name in the
    // downstream pipeline.
    private static UnifiedMaterialMetadata? ExtractGenericContext(CUE4Parse.UE4.Assets.Exports.UObject asset, string packagePath)
    {
        var metadata = new UnifiedMaterialMetadata
        {
            MaterialPath = packagePath,
            CachedParameters = MaterialCachedExpressionReader.ReadGeneric(asset),
        };
        return metadata;
    }

    // Precise material-asset predicate for the Tier 1 bridge union: a `.uasset`
    // whose NAME carries a material prefix. Name-only on purpose — the old broad
    // "path contains Material" matched 157k texture/curve/etc. files under
    // /Materials/ folders; this stays on the real material assets. MF_/MPC_ own
    // no FMaterialShaderMap (LoadMaterialShaderMapHashes returns empty — a cheap
    // wasted load), but M_/MI_/MAT_ are what recover the empty-container-entry
    // materials the container-header set misses.
    private static bool IsPrefixedMaterialAsset(GameFile file)
    {
        if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) return false;
        string name = file.Name;
        return name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MPC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase);
    }

    // Tighter than the original `Path.Contains("/Material")`. Old check
    // matched things like `/Game/UI/Materials/WBP_ShadowSample` (a
    // Widget Blueprint) which exploded inside LoadPackageObject. Here we
    // exclude obvious non-material prefixes, then accept a much wider
    // heuristic: any `Material` substring (case-insensitive) anywhere
    // in the path. Engine materials live under
    // `/Engine/Content/EngineMaterials/`, `/Engine/Content/EditorMaterials/`,
    // `/Engine/Content/EngineDebugMaterials/`, etc. — those need to load
    // because the asset-info sidecar links cooked shader-maps back to
    // them, and stripping them stripped the type-name back-fill source
    // for all engine-material shader-maps.
    private static bool IsMaterialCandidate(GameFile file)
    {
        if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string name = file.Name;
        if (name.StartsWith("WBP_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ABP_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DA_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string path = file.Path;
        // Material naming conventions (asset-side): hard accept.
        if (name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MPC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Niagara package naming conventions: `NS_` (NiagaraSystem),
        // `NE_` (NiagaraEmitter), `NSC_` / `NSCS_` (NiagaraScripts),
        // `NM_` (NiagaraModule). These compile per-stage GPU shaders that
        // ship in the same `.ushaderbytecode` archives as material shaders,
        // so without scanning them here every Niagara compute/sprite
        // shader would emit as `UnknownMaterial` even though the package
        // owning the shader-map hash is right there in the IoStore
        // container header.
        //
        // The downstream `LoadPackageObject` cast is intentionally still
        // `is UMaterialInterface` — Niagara assets fail that check so
        // they're skipped silently, but the per-package shader-map hash
        // mirror Pass020 builds (state.Root.PackageShaderMapHashes) IS
        // populated regardless. That mirror is what Pass140 + Pass150
        // walk to fill `state.NameByShaderIndex` with `NS_<name>` /
        // `NE_<name>` filename stems instead of "UnknownMaterial".
        //
        // We don't extract Niagara parameter names here — Niagara stores
        // them under FNiagaraShaderScript / FNiagaraShaderMapContent
        // (see CUE4Parse Exports/Niagara/) which has a different
        // FUniformExpressionSet equivalent that the cached-expression
        // reader doesn't probe. Adding Niagara symbol extraction is a
        // separate, larger task — see TODO at end of this file.
        if (name.StartsWith("NS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NE_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSC_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NSCS_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("NM_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Path-side accept: any path containing "Material" (case-insensitive).
        // We deliberately do NOT broaden to /FX/ or /VFX/ — those bucket
        // names are commonly used for non-Niagara content (sound cues,
        // post-process curves, prop blueprints) and the LoadPackageObject
        // failure cost on a 5-figure asset count is non-trivial.
        // Niagara packages whose names already start with NS_/NE_/NSC_
        // are picked up above; that's the supported coverage today.
        return path.Contains("Material", StringComparison.OrdinalIgnoreCase);
    }
    // TODO: Niagara symbol extraction — UNiagaraScript carries
    // `LoadedScriptResources : FNiagaraShaderScript[]` (only when
    // `Owner.Provider.ReadShaderMaps == true`); each FNiagaraShaderScript's
    // `ShaderMap` is a TShaderMap<FNiagaraShaderMapContent, ...> which
    // includes per-binding parameter info under its frozen-archive blob.
    // CUE4Parse's FNiagaraShaderMapContent currently only decodes
    // FriendlyName / DebugDescription / ShaderMapId — extending it to
    // also decode `FNiagaraDataInterfaceParamInfo[]` (and the
    // `FShaderParameterMapInfo` carried on each member FShader) would
    // give the same parameter-name table that materials carry under
    // FUniformExpressionSet. Pass020 would then call into the Niagara
    // path when `material is UNiagaraScript`, materially matching what
    // the existing UMaterialInterface branch does for FMaterialResource.

    private static UnifiedMaterialMetadata? ExtractMaterialContext(UMaterialInterface material, string materialPath)
    {
        var metadata = new UnifiedMaterialMetadata
        {
            MaterialPath = materialPath,
            // Read render-state UProperties off the material UObject. These survive
            // shipping cook because the runtime needs them for PSO setup. Source:
            // UMaterial typed fields where the asset is a UMaterial, falling back
            // to FMaterialInstanceBasePropertyOverrides for UMaterialInstance.
            // MaterialDomain/BlendableLocation are read via the property bag
            // because CUE4Parse doesn't ship typed enum mirrors for them.
            RenderState = BuildRenderState(material)
        };

        // Source 1 — inline shader-map blob (older cooks / non-IoStore).
        // When ShareCode-style external shader libraries are off, this is
        // populated and carries the full UniformExpressionSet (the gold
        // standard for parameter-name/byte-offset pairing).
        if (material.LoadedMaterialResources != null && material.LoadedMaterialResources.Count > 0)
        {
            foreach (var resource in material.LoadedMaterialResources)
            {
                if (resource.LoadedShaderMap == null)
                {
                    continue;
                }

                var shaderMap = resource.LoadedShaderMap;
                var shaderMapMetadata = new UnifiedShaderMapMetadata
                {
                    ShaderPlatform = shaderMap.ShaderPlatform.ToString(),
                    CookedShaderMapIdHash = shaderMap.ShaderMapId.CookedShaderMapIdHash?.ToString(),
                    ShaderContentHash = shaderMap.Content is FMaterialShaderMapContent materialShaderMapContent
                        ? materialShaderMapContent.ShaderContentHash.ToString()
                        : null,
                    // The library key: ResourceHash for bShareCode (external/IoStore)
                    // cooks, or Code.ResourceHash when the bytecode is inlined.
                    // This is what actually matches the archive's ShaderMapHashes.
                    ResourceHash = shaderMap.ResourceHash?.ToString() ?? shaderMap.Code?.ResourceHash.ToString(),
                };

                // ShaderMapPointerTable (type/VF hashes) is not emitted — no
                // decompile-side reader consumes it; only the on-disk archive's
                // own pointer table is used downstream.

                // NOTE: `MemoryImageResult` (FrozenObjectBase64 + frozen
                // ScriptNames/MinimalNames/VTables) is INTENTIONALLY not
                // populated. It is write-only — no decompile-side reader ever
                // consumes it — but the base64 of the raw shader-map memory
                // image is the single largest per-material payload. On the
                // master cook (22k+ materials) holding it spiked RSS to 13GB+
                // and bloated UnifiedShaderMetadata.json (which the warm-cache
                // Pass 005 must then re-read). Dropping it is a pure memory +
                // disk + warm-start win with zero symbol loss. (If a future
                // reader needs the frozen image, re-add via BuildFrozenArchive.)

                if (shaderMap.Content is FMaterialShaderMapContent materialContent)
                {
                    shaderMapMetadata.MaterialShaderMapContent = BuildShaderContent(materialContent, shaderMap.PointerTable as FShaderMapPointerTable);
                }
                // (BuildShaderContent ignores the pointer table now — kept in the
                // signature only to avoid touching the call site's null-cast.)

                metadata.LoadedShaderMaps.Add(shaderMapMetadata);
            }
        }

        // Source 2 — defensive walk of CachedExpressionData / property-bag
        // overrides / typed expression graph for parameter NAMES that
        // survive shipping cook even when the inline shader map is gone.
        // No engine-internal struct names are baked in here — the reader
        // probes-then-falls-through so custom UE forks keep working.
        metadata.CachedParameters = MaterialCachedExpressionReader.Read(material);

        // Source 3 — IoStore container-header shader-map hashes for THIS
        // material's package. Pass040 has already populated the
        // (package -> hashes) index on the export root; we copy the
        // matching list onto the material so consumers don't need to
        // round-trip through PackageShaderMapHashes when reading
        // UnifiedShaderMetadata.json.
        // The lookup is best-effort and tries a couple of path-spelling
        // variants because Pass040 keys by `gameFile.PathWithoutExtension`
        // while the caller passes a path that may or may not include the
        // `.MaterialName` object suffix.
        return metadata;
    }

    // Reads the render-state UProperties from a material UObject and returns
    // the unified DTO. Tries the typed UMaterial fields first, then
    // FMaterialInstanceBasePropertyOverrides for instances; finally probes
    // the raw property bag for fields CUE4Parse doesn't expose typed
    // (MaterialDomain, BlendableLocation, DitheredLODTransition).
    //
    // Returns null only when the asset is neither a UMaterial nor a
    // UMaterialInstance — in practice every UMaterialInterface this scan
    // sees yields at least the default surface-opaque set.
    private static UnifiedMaterialRenderState? BuildRenderState(UMaterialInterface material)
    {
        UnifiedMaterialRenderState rs = new();

        // Typed UMaterial properties (live as fields on the C# type after
        // UMaterial.Deserialize; absent when the asset is a UMaterialInstance
        // or any other UMaterialInterface subclass).
        if (material is UMaterial umat)
        {
            rs.BlendMode = umat.BlendMode.ToString();
            rs.ShadingModel = umat.ShadingModel.ToString();
            rs.TranslucencyLightingMode = umat.TranslucencyLightingMode.ToString();
            rs.TwoSided = umat.TwoSided;
            rs.DisableDepthTest = umat.bDisableDepthTest;
            rs.IsMasked = umat.bIsMasked;
            rs.OpacityMaskClipValue = umat.OpacityMaskClipValue;
        }

        // Instance-level overrides take precedence over the parent's UMaterial
        // values when present. UMaterialInstance carries a typed
        // FMaterialInstanceBasePropertyOverrides struct; UE only writes
        // members that were actually overridden in editor.
        if (material is UMaterialInstance instance && instance.BasePropertyOverrides != null)
        {
            rs.HasInstanceOverrides = true;
            rs.BlendModeOverridden = true;
            rs.BlendMode = instance.BasePropertyOverrides.BlendMode.ToString();
            rs.ShadingModelOverridden = true;
            rs.ShadingModel = instance.BasePropertyOverrides.ShadingModel.ToString();
            rs.OpacityMaskClipValueOverridden = true;
            rs.OpacityMaskClipValue = instance.BasePropertyOverrides.OpacityMaskClipValue;
            rs.DitheredLODTransition = instance.BasePropertyOverrides.DitheredLODTransition;

            // Walk one level up through Parent to fill in fields the override
            // struct doesn't carry (TwoSided, DisableDepthTest, IsMasked,
            // TranslucencyLightingMode). UE evaluates these from the parent
            // material at runtime when the instance doesn't override them.
            if (instance.Parent is UMaterial parentMat)
            {
                if (!rs.TwoSided) rs.TwoSided = parentMat.TwoSided;
                if (!rs.DisableDepthTest) rs.DisableDepthTest = parentMat.bDisableDepthTest;
                if (!rs.IsMasked) rs.IsMasked = parentMat.bIsMasked;
                rs.TranslucencyLightingMode = parentMat.TranslucencyLightingMode.ToString();
            }
        }

        // Property-bag probes for fields without a typed CUE4Parse mirror.
        // GetOrDefault<FName> returns the raw enum literal name as text on
        // byte-backed enum properties; empty when the property wasn't
        // serialised (i.e. the editor default applies).
        if (material.TryGetValue(out FName domainName, "MaterialDomain") && !domainName.IsNone)
        {
            rs.MaterialDomain = domainName.Text;
        }
        if (material.TryGetValue(out FName blendableLoc, "BlendableLocation") && !blendableLoc.IsNone)
        {
            rs.BlendableLocation = blendableLoc.Text;
        }
        if (!rs.DitheredLODTransition && material.TryGetValue(out bool dithered, "DitheredLODTransition"))
        {
            rs.DitheredLODTransition = dithered;
        }

        return rs;
    }

    private static UnifiedPointerTable BuildPointerTable(FShaderMapPointerTable pointerTable)
    {
        var result = new UnifiedPointerTable();

        if (pointerTable.Types != null)
        {
            result.Types = pointerTable.Types.Select(type => new UnifiedHashName
            {
                Hash = type.Hash.ToString("X16")
            }).ToList();
        }

        if (pointerTable.VFTypes != null)
        {
            result.VertexFactoryTypes = pointerTable.VFTypes.Select(type => new UnifiedHashName
            {
                Hash = type.Hash.ToString("X16")
            }).ToList();
        }

        if (pointerTable.TypeDependencies != null)
        {
            result.TypeDependencies = pointerTable.TypeDependencies.Select(type => new UnifiedTypeDependency
            {
                Name = type.Name?.ToString() ?? string.Empty,
                SavedLayoutSize = type.SavedLayoutSize,
                SavedLayoutHash = type.SavedLayoutHash.ToString()
            }).ToList();
        }

        return result;
    }

    // BuildFrozenArchive was removed: its only caller (the MemoryImageResult
    // population in ExtractMaterialContext) is gone because that payload is
    // write-only / never read by any decompile-side consumer, and the base64
    // memory image was the dominant per-material memory + disk cost. The DTO
    // types (UnifiedFrozenArchive/Name/VTable) remain in the schema for
    // backward-compatible deserialize of older files; re-add this builder if a
    // future reader genuinely needs the frozen image.

    // `UniformExpressionSet` carries the material parameter names / preshader
    // data the .shader `Properties` block needs — always emitted.
    //
    // `Shaders[]` (base `FShaderMapContent.Shaders`) is emitted for
    // completeness but is EMPTY for this cook's bShareCode materials —
    // verified empirically (frozen memory-image blob is 20-38KB of real
    // bytes, not truncated/zero, yet the base-class Shaders/ShaderTypes/
    // ShaderPermutations/ShaderHash arrays all deserialize to zero length).
    // UE nests the REAL per-shader graph under `OrderedMeshShaderMaps[i]`
    // instead: each entry is itself a `FShaderMapContent` subclass
    // (`FMeshMaterialShaderMap`, one per vertex-factory permutation this
    // material compiled against) carrying its OWN Shaders/ShaderTypes/
    // ShaderPermutations — confirmed non-empty (a VAT character material's
    // OrderedMeshShaderMaps[0] alone held 23-51 real FShader entries with
    // populated `ParameterMapInfo.LooseParameterBuffers`). THIS is what
    // Pass 050's `AppendShaderTruthRecords` already has a loop for (it just
    // had nothing to read before `BuildMeshShaderMap` existed) and what
    // Pass 165/Pass 180's $Globals reconciliation needs.
    //
    // Per-shader cost stays lean: `BuildShader` only populates ResourceIndex
    // (the Pass 050 dictionary join key)/TypeHash/VertexFactoryTypeHash/
    // ParameterMapInfo — `Bindings` (full per-parameter byte-offset detail)
    // stays dropped, that was the dominant size on the OLD full-provider
    // material scan (23k materials × 100s of shaders, 11GB+). That scan is
    // gone: Pass 030's Tier 2 enrich only fully-loads the materials the
    // CURRENT archive references (see BuildMaterialContexts), so the
    // persisted unified file's material set is bounded by exports, not by
    // the whole game.
    private static UnifiedShaderContent BuildShaderContent(FMaterialShaderMapContent content, FShaderMapPointerTable? pointerTable)
    {
        return new UnifiedShaderContent
        {
            UniformExpressionSet = BuildUniformExpressionSet(content.MaterialCompilationOutput?.UniformExpressionSet),
            Shaders = content.Shaders?.Select(BuildShader).ToList() ?? new List<UnifiedShader>(),
            OrderedMeshShaderMaps = content.OrderedMeshShaderMaps?.Select(m => m == null ? new UnifiedOrderedMeshShaderMap() : BuildMeshShaderMap(m)).ToList() ?? new List<UnifiedOrderedMeshShaderMap>(),
        };
    }

    private static UnifiedUniformExpressionSet? BuildUniformExpressionSet(FUniformExpressionSet? uniformExpressionSet)
    {
        if (uniformExpressionSet == null)
        {
            return null;
        }

        return new UnifiedUniformExpressionSet
        {
            UniformPreshaders = uniformExpressionSet.UniformPreshaders?.Select(BuildPreshaderHeader).ToList() ?? new List<UnifiedMaterialUniformPreshaderHeader>(),
            UniformPreshaderFields = uniformExpressionSet.UniformPreshaderFields?.Select(field => new UnifiedMaterialUniformPreshaderField
            {
                BufferOffset = field.BufferOffset,
                ComponentIndex = field.ComponentIndex,
                Type = field.Type.ToString()
            }).ToList() ?? new List<UnifiedMaterialUniformPreshaderField>(),
            UniformNumericParameters = uniformExpressionSet.UniformNumericParameters?.Select(parameter => new UnifiedMaterialNumericParameter
            {
                ParameterName = parameter.ParameterInfo.Name.Text,
                Association = parameter.ParameterInfo.Association.ToString(),
                Index = parameter.ParameterInfo.Index,
                ParameterType = parameter.ParameterType.ToString(),
                DefaultValueOffset = parameter.DefaultValueOffset,
                Value = ConvertMaterialParameterValue(parameter.Value)
            }).ToList() ?? new List<UnifiedMaterialNumericParameter>(),
            UniformTextureParameters = uniformExpressionSet.UniformTextureParameters?.Select(textureParameters =>
                textureParameters?.Select(BuildTextureParameterInfo).ToList() ?? new List<UnifiedMaterialTextureParameter>()).ToList()
                ?? new List<List<UnifiedMaterialTextureParameter>>(),
            UniformExternalTextureParameters = uniformExpressionSet.UniformExternalTextureParameters?.Select(parameter => new UnifiedMaterialExternalTextureParameter
            {
                ParameterName = parameter.ParameterName.Text,
                ExternalTextureGuid = parameter.ExternalTextureGuid.ToString(),
                SourceTextureIndex = parameter.SourceTextureIndex
            }).ToList() ?? new List<UnifiedMaterialExternalTextureParameter>(),
            UniformTextureCollectionParameters = uniformExpressionSet.UniformTextureCollectionParameters?.Select(parameter => new UnifiedMaterialTextureCollectionParameter
            {
                TextureCollectionIndex = parameter.TextureCollectionIndex,
                ParameterName = parameter.ParameterInfo.Name.ToString(),
                Association = parameter.ParameterInfo.Association.ToString(),
                Index = parameter.ParameterInfo.Index,
                IsVirtualCollection = parameter.bisVirtualCollection
            }).ToList() ?? new List<UnifiedMaterialTextureCollectionParameter>(),
            ParameterCollections = uniformExpressionSet.ParameterCollections?.Select(guid => guid.ToString()).ToList() ?? new List<string>(),
            UniformPreshaderBufferSize = uniformExpressionSet.UniformPreshaderBufferSize,
            UniformBufferLayoutInitializer = BuildUniformBufferLayoutInitializer(uniformExpressionSet.UniformBufferLayoutInitializer),
            UniformPreshaderData = BuildPreshaderData(uniformExpressionSet.UniformPreshaderData)
        };
    }

    private static UnifiedMaterialTextureParameter BuildTextureParameterInfo(FMaterialTextureParameterInfo parameter)
    {
        return new UnifiedMaterialTextureParameter
        {
            ParameterName = GetMaterialParameterName(parameter),
            Association = GetMaterialParameterAssociation(parameter),
            Index = GetMaterialParameterIndex(parameter),
            TextureIndex = parameter.TextureIndex,
            SamplerSource = parameter.SamplerSource.ToString(),
            VirtualTextureLayerIndex = parameter.VirtualTextureLayerIndex
        };
    }

    private static UnifiedUniformBufferLayoutInitializer BuildUniformBufferLayoutInitializer(FRHIUniformBufferLayoutInitializer layout)
    {
        return new UnifiedUniformBufferLayoutInitializer
        {
            Name = layout.Name,
            Resources = BuildUniformBufferResources(layout.Resources),
            GraphResources = BuildUniformBufferResources(layout.GraphResources),
            GraphTextures = BuildUniformBufferResources(layout.GraphTextures),
            GraphBuffers = BuildUniformBufferResources(layout.GraphBuffers),
            GraphUniformBuffers = BuildUniformBufferResources(layout.GraphUniformBuffers),
            UniformBuffers = BuildUniformBufferResources(layout.UniformBuffers),
            Hash = layout.Hash,
            ConstantBufferSize = layout.ConstantBufferSize,
            RenderTargetsOffset = layout.RenderTargetsOffset,
            StaticSlot = layout.StaticSlot,
            BindingFlags = layout.BindingFlags.ToString(),
            HasNonGraphOutputs = layout.Flags.HasFlag(ERHIUniformBufferFlags.HasNonGraphOutputs),
            NoEmulatedUniformBuffer = layout.Flags.HasFlag(ERHIUniformBufferFlags.NoEmulatedUniformBuffer),
            UniformView = layout.Flags.HasFlag(ERHIUniformBufferFlags.UniformView)
        };
    }

    private static List<UnifiedUniformBufferResource> BuildUniformBufferResources(FRHIUniformBufferResource[]? resources)
    {
        return resources?.Select(resource => new UnifiedUniformBufferResource
        {
            MemberOffset = resource.MemberOffset,
            MemberType = resource.MemberType.ToString()
        }).ToList() ?? new List<UnifiedUniformBufferResource>();
    }

    private static string GetMaterialParameterName(FMaterialBaseParameterInfo parameter)
    {
        if (parameter.ParameterInfo != null)
        {
            return parameter.ParameterInfo.Name.Text;
        }

        if (parameter.ParameterInfoOld != null)
        {
            return parameter.ParameterInfoOld.Name.ToString();
        }

        return parameter.ParameterName ?? string.Empty;
    }

    private static string GetMaterialParameterAssociation(FMaterialBaseParameterInfo parameter)
    {
        if (parameter.ParameterInfo != null)
        {
            return parameter.ParameterInfo.Association.ToString();
        }

        if (parameter.ParameterInfoOld != null)
        {
            return parameter.ParameterInfoOld.Association.ToString();
        }

        return string.Empty;
    }

    private static int GetMaterialParameterIndex(FMaterialBaseParameterInfo parameter)
    {
        if (parameter.ParameterInfo != null)
        {
            return parameter.ParameterInfo.Index;
        }

        if (parameter.ParameterInfoOld != null)
        {
            return parameter.ParameterInfoOld.Index;
        }

        return 0;
    }

    private static UnifiedMaterialUniformPreshaderHeader BuildPreshaderHeader(FMaterialUniformPreshaderHeader header)
    {
        var result = new UnifiedMaterialUniformPreshaderHeader
        {
            OpcodeOffset = header.OpcodeOffset,
            OpcodeSize = header.OpcodeSize
        };

        if (header is FMaterialUniformPreshaderHeader_5_1 header51)
        {
            result.FieldIndex = header51.FieldIndex;
            result.NumFields = header51.NumFields;
        }

        if (header is FMaterialUniformPreshaderHeader_5_0 header50)
        {
            result.BufferOffset = header50.BufferOffset;
            result.ComponentType = header50.ComponentType.ToString();
            result.NumComponents = header50.NumComponents;
        }

        if (header is FMaterialUniformPreshaderHeader_5_8 header58)
        {
            result.BufferOffset = header58.BufferOffset;
            result.Type = header58.Type.ToString();
        }

        return result;
    }

    private static UnifiedMaterialPreshaderData BuildPreshaderData(FMaterialPreshaderData preshaderData)
    {
        return new UnifiedMaterialPreshaderData
        {
            Names = preshaderData.Names?.Select(name => name.Text).ToList() ?? new List<string>(),
            NamesOffset = preshaderData.NamesOffset?.ToList() ?? new List<uint>(),
            StructTypes = preshaderData.StructTypes?.Select(type => new UnifiedPreshaderStructType
            {
                Hash = type.Hash.ToString("X16"),
                ComponentTypeIndex = type.ComponentTypeIndex,
                NumComponents = type.NumComponents
            }).ToList() ?? new List<UnifiedPreshaderStructType>(),
            StructComponentTypes = preshaderData.StructComponentTypes?.Select(type => type.ToString()).ToList() ?? new List<string>(),
            Data = Convert.ToBase64String(preshaderData.Data ?? Array.Empty<byte>()),
            IsPreshader2 = preshaderData.bPreshader2
        };
    }

    private static object? ConvertMaterialParameterValue(object? value)
    {
        return value switch
        {
            null => null,
            FLinearColor color => new UnifiedLinearColor
            {
                R = color.R,
                G = color.G,
                B = color.B,
                A = color.A
            },
            FVector4 vector => new UnifiedVector4
            {
                X = (double)vector.X,
                Y = (double)vector.Y,
                Z = (double)vector.Z,
                W = (double)vector.W
            },
            _ => value
        };
    }

    // Emits ResourceIndex/TypeHash/VertexFactoryTypeHash/ParameterMapInfo —
    // everything Pass 050's `AppendShaderTruthRecords` and Pass 165's join key
    // on. `Bindings` (per-parameter byte-offset detail beyond the loose-buffer
    // summary ParameterMapInfo already carries) stays dropped — it was the
    // dominant size contributor on the old full-provider scan and nothing
    // downstream reads it. `ResourceIndex` is the CRITICAL field: it's the
    // shader's slot within its OWNING shader-map (0..NumShaders-1), assigned
    // by the cooker — NOT the array position in whichever CUE4Parse list
    // (`content.Shaders` vs `content.OrderedMeshShaderMaps[i].Shaders`) it
    // happens to live in. Pass 050 already keys its truth dictionary on this
    // value (`BuildTruthByResourceIndex`), so populating it here is what makes
    // that ALREADY-WRITTEN matching logic work instead of silently matching
    // nothing.
    private static UnifiedShader BuildShader(FShader shader)
    {
        return new UnifiedShader
        {
            ResourceIndex = shader.ResourceIndex,
            NumInstructions = shader.NumInstructions,
            SortKey = shader.SortKey,
            TypeHash = ResolveIndexedTypeHash(shader.Type),
            VertexFactoryTypeHash = ResolveIndexedTypeHash(shader.VFType),
            ParameterMapInfo = BuildShaderParameterMapInfo(shader.ParameterMapInfo),
        };
    }

    // Builds the per-vertex-factory shader collection. THIS is where a
    // material's real per-shader graph lives for this cook — the outer
    // `content.Shaders[]` (base `FShaderMapContent.Shaders`) is empty here;
    // UE nests the actual VS/PS/etc per vertex-factory permutation under
    // `FMaterialShaderMapContent.OrderedMeshShaderMaps[i]` (itself a
    // `FShaderMapContent` subclass carrying its OWN Shaders/ShaderTypes/
    // ShaderPermutations arrays), confirmed empirically: a VAT character
    // material's OrderedMeshShaderMaps[0] alone carried 23-51 real FShader
    // entries with non-empty ParameterMapInfo.LooseParameterBuffers, while
    // the outer content.Shaders was always length 0. `ReadArrayOfPtrs` can
    // leave individual slots null (unfrozen pointer) — those become a blank
    // placeholder UnifiedShader so positional alignment with ShaderTypes[]/
    // ShaderPermutations[] is preserved for the Math.Min-bounded pairing in
    // AppendShaderTruthRecords.
    private static UnifiedOrderedMeshShaderMap BuildMeshShaderMap(FMeshMaterialShaderMap meshMap)
    {
        return new UnifiedOrderedMeshShaderMap
        {
            VertexFactoryType = new UnifiedHashName { Hash = ResolveIndexedTypeHash(meshMap.VertexFactoryTypeName) },
            ShaderTypes = meshMap.ShaderTypes?.Select(t => new UnifiedHashName { Hash = ResolveIndexedTypeHash(t) }).ToList() ?? new List<UnifiedHashName>(),
            ShaderPermutations = meshMap.ShaderPermutations?.ToList() ?? new List<int>(),
            Shaders = meshMap.Shaders?.Select(s => s == null ? new UnifiedShader() : BuildShader(s)).ToList() ?? new List<UnifiedShader>(),
        };
    }

    private static string ResolveIndexedTypeHash(FHashedName hashedName)
    {
        return hashedName.Hash != 0 ? hashedName.Hash.ToString("X16") : string.Empty;
    }

    private static UnifiedShaderBindings BuildShaderBindings(FShaderParameterBindings bindings)
    {
        return new UnifiedShaderBindings
        {
            Parameters = bindings.Parameters?.Select(parameter => new UnifiedBindingParameter
            {
                BufferIndex = parameter.BufferIndex,
                BaseIndex = parameter.BaseIndex,
                ByteOffset = parameter.ByteOffset,
                ByteSize = parameter.ByteSize
            }).ToList() ?? new List<UnifiedBindingParameter>(),
            ResourceParameters = bindings.ResourceParameters?.Select(parameter => new UnifiedResourceBindingParameter
            {
                ByteOffset = parameter.ByteOffset,
                BaseIndex = checked((byte)parameter.BaseIndex),
                BaseType = parameter.BaseType.ToString()
            }).ToList() ?? new List<UnifiedResourceBindingParameter>(),
            BindlessResourceParameters = bindings.BindlessResourceParameters?.Select(parameter => new UnifiedBindlessResourceParameter
            {
                ByteOffset = parameter.ByteOffset,
                GlobalConstantOffset = parameter.GlobalConstantOffset,
                BaseType = parameter.BaseType.ToString()
            }).ToList() ?? new List<UnifiedBindlessResourceParameter>(),
            GraphUniformBuffers = bindings.GraphUniformBuffers?.Select(parameter => new UnifiedParameterStructReference
            {
                BufferIndex = parameter.BufferIndex,
                ByteOffset = parameter.ByteOffset
            }).ToList() ?? new List<UnifiedParameterStructReference>(),
            ParameterReferences = bindings.ParameterReferences?.Select(parameter => new UnifiedParameterStructReference
            {
                BufferIndex = parameter.BufferIndex,
                ByteOffset = parameter.ByteOffset
            }).ToList() ?? new List<UnifiedParameterStructReference>(),
            StructureLayoutHash = bindings.StructureLayoutHash,
            RootParameterBufferIndex = bindings.RootParameterBufferIndex
        };
    }

    private static UnifiedShaderParameterMapInfo BuildShaderParameterMapInfo(FShaderParameterMapInfo parameterMapInfo)
    {
        return new UnifiedShaderParameterMapInfo
        {
            UniformBuffers = parameterMapInfo.UniformBuffers?.Select(parameter => new UnifiedShaderParameterInfo
            {
                BaseIndex = parameter.BaseIndex,
                Size = parameter.Size
            }).ToList() ?? new List<UnifiedShaderParameterInfo>(),
            TextureSamplers = parameterMapInfo.TextureSamplers?.Select(parameter => new UnifiedShaderResourceParameterInfo
            {
                BaseIndex = parameter.BaseIndex,
                Size = parameter.Size,
                BufferIndex = parameter is FShaderResourceParameterInfo resource ? resource.BufferIndex : (byte)0,
                Type = parameter is FShaderResourceParameterInfo typed ? (byte)typed.Type : (byte)0
            }).ToList() ?? new List<UnifiedShaderResourceParameterInfo>(),
            SRVs = parameterMapInfo.SRVs?.Select(parameter => new UnifiedShaderResourceParameterInfo
            {
                BaseIndex = parameter.BaseIndex,
                Size = parameter.Size,
                BufferIndex = parameter is FShaderResourceParameterInfo resource ? resource.BufferIndex : (byte)0,
                Type = parameter is FShaderResourceParameterInfo typed ? (byte)typed.Type : (byte)0
            }).ToList() ?? new List<UnifiedShaderResourceParameterInfo>(),
            LooseParameterBuffers = parameterMapInfo.LooseParameterBuffers?.Select(buffer => new UnifiedShaderLooseParameterBufferInfo
            {
                BaseIndex = buffer.BaseIndex,
                Size = buffer.Size,
                Parameters = buffer.Parameters?.Select(parameter => new UnifiedShaderParameterInfo
                {
                    BaseIndex = parameter.BaseIndex,
                    Size = parameter.Size
                }).ToList() ?? new List<UnifiedShaderParameterInfo>()
            }).ToList() ?? new List<UnifiedShaderLooseParameterBufferInfo>(),
            Hash = parameterMapInfo.Hash.ToString("X16")
        };
    }
}
