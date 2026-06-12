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

    // Two scan modes, chosen by what's available on `state`:
    //
    // 1. **Hash-scoped scan** (preferred) — when Pass010 stashed the current
    //    archive's shader-map hashes AND Pass040 already built the
    //    package -> hash index, walk only the packages whose hashes
    //    intersect. On a 5k+ asset game this turns a multi-minute scan
    //    into a few seconds because each shader-archive only references
    //    the materials in its chunk.
    //
    // 2. **Full provider scan** (fallback) — older non-IoStore paks (or
    //    games that don't populate the per-package hash list in the
    //    container header) leave Pass040's index empty. We fall back to
    //    walking every UAsset and filter by `IsMaterialCandidate`. Same
    //    behaviour as the original implementation, just guarded so we
    //    don't take the slow path when the fast path is available.
    //
    // Both modes funnel through `LoadAndCacheMaterial` so the same
    // (PathWithoutExtension -> UnifiedMaterialMetadata?) cache is
    // reused across multiple archive exports in the same FModel session.
    // A null cache value is a "tried and failed" marker — don't retry.
    private static void BuildMaterialContexts(ExportPipelineState state, AbstractVfsFileProvider provider)
    {
        var output = state.Root;
        var log = state.Log;
        var cache = state.LoadedMaterialCache;
        var archiveHashes = state.CurrentArchiveShaderMapHashes;
        var packageHashIndex = state.Root.PackageShaderMapHashes;

        if (archiveHashes.Count > 0 && packageHashIndex.Count > 0)
        {
            // 1. Collect the intersecting candidates (cheap; the hash-set
            //    membership test is the only per-package work here).
            var candidateList = new List<KeyValuePair<string, List<string>>>();
            foreach (KeyValuePair<string, List<string>> kvp in packageHashIndex)
            {
                if (HashesIntersect(kvp.Value, archiveHashes)) candidateList.Add(kvp);
            }
            int candidates = candidateList.Count;

            long reused = 0;
            long loaded = 0;
            long extracted = 0;
            long loadFailures = 0;

            // 2. Load + extract the cache-misses in PARALLEL. Each
            //    provider.LoadPackage is AES-decrypt + zstd/oodle-decompress +
            //    full deserialize — IO + crypto bound, identical to Pass 035's
            //    walk, so it parallelises the same way and the same ~8-way cap
            //    saturates the disk/crypto without thrashing. The cache is a
            //    ConcurrentDictionary so worker writes are safe; output is
            //    merged single-threaded afterwards.
            int parallelism = Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));
            System.Threading.Tasks.Parallel.ForEach(
                candidateList,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
                kvp =>
                {
                    string packagePath = kvp.Key;
                    if (cache.ContainsKey(packagePath)) { System.Threading.Interlocked.Increment(ref reused); return; }

                    UnifiedMaterialMetadata? metadata = LoadAndExtractByPath(provider, packagePath, out bool loadedOk, out bool failed);
                    if (loadedOk) System.Threading.Interlocked.Increment(ref loaded);
                    if (failed) System.Threading.Interlocked.Increment(ref loadFailures);

                    // Copy the IoStore-derived shader-map hashes onto the
                    // material so the unified file is self-contained.
                    // PackageShaderMapHashes is the AUTHORITATIVE bridge for
                    // IoStore cooks (modern UE5) — without this copy the
                    // consumer can't link back from a shader-archive hash to a
                    // material when LoadedShaderMaps is empty.
                    if (metadata != null)
                    {
                        metadata.PackageShaderMapHashes = new List<string>(kvp.Value);
                        System.Threading.Interlocked.Increment(ref extracted);
                    }
                    cache[packagePath] = metadata;
                });

            // 3. Merge into the cumulative output single-threaded — both
            //    freshly-extracted and (Pass 005) cache-seeded non-null entries.
            //    `produced` gates the full-scan fallback so a fully cache-warmed
            //    scan (extracted==0 but everything reused) does NOT re-walk.
            int produced = 0;
            foreach (KeyValuePair<string, List<string>> kvp in candidateList)
            {
                if (cache.TryGetValue(kvp.Key, out UnifiedMaterialMetadata? m) && m != null)
                {
                    output.MaterialInterfaces[kvp.Key] = m;
                    produced++;
                }
            }

            log($"    Material scan (hash-scoped): archive-hashes={archiveHashes.Count}, candidates={candidates}, cache-reused={reused}, loaded={loaded}, extracted={extracted}, produced={produced}, skipped-on-error={loadFailures}.");

            // Hash-scoped scan produced zero materials despite having
            // both archive hashes and a package-hash index — likely a
            // hash-format/casing mismatch between the two sources, or a
            // session timing issue where Pass020 hadn't fully populated
            // the index yet. Fall back to the full provider walk so the
            // unified file isn't shipped with `MaterialInterfaces: {}`
            // — that leaves every per-material reader empty and every
            // Material CB anonymous downstream (root cause documented
            // per UE_SYMBOL_SOURCES.md). NOTE: gate on `produced` (extracted +
            // cache-reused) so a fully cache-warmed scan doesn't re-walk.
            if (produced == 0 && candidates > 0)
            {
                log($"    Material scan (hash-scoped): produced ZERO materials (candidates={candidates}) — falling back to full provider scan.");
                FullProviderScan(provider, output, cache, log);
            }
            return;
        }

        // Fallback path — no IoStore hash index available, do the full
        // provider walk. Cache is still honoured so re-export of the
        // same archive in a session is cheap.
        FullProviderScan(provider, output, cache, log);
    }

    private static void FullProviderScan(AbstractVfsFileProvider provider, UnifiedShaderMetadataRoot output, ConcurrentDictionary<string, UnifiedMaterialMetadata?> cache, Action<string> log)
    {
        // Pre-filter to the material-candidate list so the parallel partition
        // sizes correctly and the considered-count is exact.
        var candidates = provider.Files.Values.Where(IsMaterialCandidate).ToList();

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

    // Linear scan against the archive's hash set. The archive set is
    // typically a few thousand hashes, the package list is shorter
    // (one-or-two hashes per package), so this beats building a hash
    // map for the small inner list.
    private static bool HashesIntersect(List<string> packageHashes, HashSet<string> archiveHashes)
    {
        for (int i = 0; i < packageHashes.Count; i++)
        {
            if (archiveHashes.Contains(packageHashes[i])) return true;
        }
        return false;
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

    // Only the two fields any decompile-side reader actually consumes are
    // emitted: `UniformExpressionSet` (material parameter names / preshader
    // data) and `Shaders[]` (Pass 165 joins it by ARRAY ORDER to attach each
    // shader's ParameterMapInfo — see BuildShader, which now emits only that).
    // The dropped arrays (`ShaderTypeHashes`, `ShaderPermutations`,
    // `ShaderPipelines`, `OrderedMeshShaderMaps`) plus the per-shader binding
    // detail were the dominant size of the unified file — on the master cook
    // (23k materials, each with 100s of shaders) they ballooned it to 11GB+,
    // which made the warm-cache read (Pass 005) and the decompile-side read
    // (Pass 140/160) pathologically slow. None of them is read anywhere
    // downstream. ShaderPipelines/OrderedMeshShaderMaps shaders are NOT folded
    // into `Shaders[]` because Pass165 joins against the on-disk shader-map's
    // primary Shaders[] order only.
    private static UnifiedShaderContent BuildShaderContent(FMaterialShaderMapContent content, FShaderMapPointerTable? pointerTable)
    {
        // ONLY `UniformExpressionSet` is emitted — it carries the material
        // parameter names + preshader data that the .shader `Properties` block
        // needs. `Shaders[]` (per-shader `ParameterMapInfo`) was ALSO dropped:
        // on the master cook (23k materials × 100s of shaders) it was the bulk
        // of the unified file, pushing it to 3GB+ — and any unified that large
        // is UNUSABLE downstream, because the decompile-side readers
        // (`UnifiedMaterialReader.LoadFromFile`, Pass 140) materialise the whole
        // document and a >2GB JSON exceeds .NET's single-string / JsonDocument
        // limits (observed: "Insufficient memory", then dxil-spirv -4 from the
        // starved native heap). The cost is that Pass 165's $Globals byte-offset
        // join goes quiet, so loose `$Globals` members fall back to the
        // anonymous `_Globals_m0[N]` form — a contained symbol-QUALITY
        // regression, not a material-LINKAGE one. TODO(top-tier): restore it by
        // writing per-shader ParameterMapInfo into the per-archive
        // `.stableinfo.json` sidecar (Pass 165 already joins per-archive), which
        // keeps the cross-library unified file lean.
        return new UnifiedShaderContent
        {
            UniformExpressionSet = BuildUniformExpressionSet(content.MaterialCompilationOutput?.UniformExpressionSet),
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

    // Slimmed: only `ParameterMapInfo` is emitted (the one field Pass 165
    // reads — it joins by array order, so the per-shader index/hash/binding
    // detail is unnecessary). Dropping `Bindings` + the hashes off every
    // shader is the bulk of the unified-file size reduction. `pointerTable` is
    // no longer needed (it only fed the dropped type-hash resolution).
    private static UnifiedShader BuildShader(FShader shader)
    {
        return new UnifiedShader
        {
            ParameterMapInfo = BuildShaderParameterMapInfo(shader.ParameterMapInfo)
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
