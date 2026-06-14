using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Top-level orchestrator for `--export-map-direct`. Responsibilities split
// across three layers that all run in the SAME pass so the output package is
// internally consistent (a manifest count never references an actor that was
// never written):
//
//   (A) RENDER LAYER  — Iterate every collected actor, fan it out through
//       ComponentResolver into PlacedComponent, dispatch each placement
//       through the IComponentExporter registry. Mesh / spline / light /
//       camera / landscape all write into ONE shared GlbSceneContext, which
//       part-flushes at MaxInstancesPerGlb. Result: `<map>.glb` (or
//       `<map>.partNNN.glb`) + material sidecars.
//   (B) LOSSLESS LAYER — Walk the SAME actor list and for every actor write
//       a JSON dump under `Actors/`. This is independent of (A): an actor
//       with no renderable component still produces a JSON. So Niagara,
//       PostProcessVolume, SkyAtmosphere, DataLayer info, PlayerStart all
//       round-trip without loss.
//   (C) CLOSURE LAYER — Walk the map package's import set, recursively
//       export every referenced asset under `Assets/`. Foundation revision
//       writes a manifest counter; the real recursion lands in the cell.
//
// A scene-manifest.json at the run root ties the three layers together with
// per-layer counts plus a `dropped` list (the user requirement is
// dropped == 0; the manifest is the audit trail).
//
// The Export() signature stays identical to the prior monolithic version so
// callers (UE_GlbSceneExport_Hook + the CLI's RunExportMapDirect) need no
// changes.
public sealed class WorldGlbExporter
{
    // Render-layer dispatch table. Spline BEFORE static (USplineMeshComponent
    // : UStaticMeshComponent, the first CanExport hit wins). Static between
    // them and the actor-level landscape entry so component-bearing actors
    // exhaust the component options before the actor-as-leaf landscape
    // entry has a chance.
    private static IComponentExporter[] BuildExporterRegistry() => new IComponentExporter[]
    {
        new SplineMeshComponentExporter(),
        new StaticMeshComponentExporter(),
        new LightComponentExporter(),
        new CameraComponentExporter(),
        new LandscapeComponentExporter(),
    };

    private readonly IFileProvider _provider;
    private readonly ExporterOptions _options;
    private readonly Action<string> _log;
    private readonly Action<string> _logError;
    private readonly IComponentExporter[] _exporterRegistry;

    public WorldGlbExporter(IFileProvider provider, ExporterOptions options, Action<string> log, Action<string> logError)
    {
        _provider = provider;
        _options = options;
        _log = log;
        _logError = logError;
        _exporterRegistry = BuildExporterRegistry();
    }

    public bool Export(UWorld world, string sourcePackageKey, string outputDirectory, CancellationToken cancellationToken)
    {
        string worldPackagePath = world.Owner?.Name ?? world.GetPathName();
        // The provider Files key (physical/virtual path, e.g.
        // "Oni_Valley_VFX/Content/.../Oni_Valley") is what the World Partition
        // cell + external-actor packages are keyed by; world.Owner.Name is the
        // logical "/Game/..." mount path, which does NOT match Files keys. Use
        // the file key (minus extension) for the path-based cell scans.
        string scanKey = StripExtension(sourcePackageKey);
        _log($"[GlbScene] Exporting world '{worldPackagePath}' (file key '{scanKey}') ...");

        string outputBasePath = BuildOutputBase(outputDirectory, worldPackagePath);
        string actorsOutputDirectory = outputBasePath + "_Actors";
        string assetsOutputDirectory = outputBasePath + "_Assets";
        string manifestPath = outputBasePath + ".scene-manifest.json";

        var collector = new WorldActorCollector(_provider, _log, _logError, cancellationToken);
        List<WorldActor> actors = collector.Collect(world, scanKey);
        SceneCensus.Log(actors, _log);

        // Diagnostic gate: RURI_GLB_CENSUS_ONLY=1 dumps the census (plus a
        // deep structural sample of the named ExportTypes) and returns before
        // the heavy geometry build, so scene-coverage iteration is fast.
        if (Environment.GetEnvironmentVariable("RURI_GLB_CENSUS_ONLY") is "1")
        {
            string? sampleList = Environment.GetEnvironmentVariable("RURI_GLB_CENSUS_SAMPLES");
            if (!string.IsNullOrWhiteSpace(sampleList))
            {
                SceneCensus.DumpSamples(
                    actors,
                    _log,
                    sampleList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            _log("[GlbScene] RURI_GLB_CENSUS_ONLY set — skipping geometry build.");
            return true;
        }

        // Build the shared scene context and the manifest the three layers
        // populate as they run.
        var manifest = new SceneManifest
        {
            SourceMapPackagePath = worldPackagePath,
            GameVersion = _provider.Versions?.Game.ToString() ?? string.Empty,
        };
        var materialFactory = new GlbMaterialFactory(_log, _logError);
        var context = new GlbSceneContext(_provider, _options, _log, _logError, materialFactory, manifest);
        context.SetOutputBasePath(outputBasePath);

        // (A) Render layer ----------------------------------------------------
        _log($"[GlbScene] Building scene in <= {GlbSceneContext.MaxInstancesPerGlb}-instance .glb parts...");
        foreach (var placement in actors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DispatchActor(placement.Actor, placement.BaseTransform, context);
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Actor '{(placement.Actor as UObject)?.Name}' failed: {ex.Message}");
                manifest.RecordDroppedActor($"{(placement.Actor as UObject)?.GetPathName() ?? "?"}: {ex.Message}");
            }
        }
        context.FlushBatch();
        // Punctual lights AND cameras cannot ride the SceneBuilder in this
        // SharpGLTF build (LightBuilder ctors are internal; SceneBuilder.AddCamera
        // is a no-op through ToGltf2), so both were buffered across the pass and
        // are emitted now, after the final mesh flush, into one dedicated .glb
        // part at the Schema2 layer — complete set, written exactly once.
        context.WritePendingLightsAndCameras();
        _log($"[GlbScene][Diag] resolver yielded {_instrumentation.Resolved} components (claimed={_instrumentation.Claimed}, unclaimed={_instrumentation.Unclaimed}); pendingLights={context.PendingLightCount} pendingCameras={context.PendingCameraCount}.");

        // The "271 components used per-instance OverrideMaterials" diagnostic
        // line from the prior monolith is no longer needed: GlbSceneContext
        // now folds the override material set into the mesh-share key, so
        // overrides correctly emit distinct MeshBuilders and the previously
        // skipped placements now contribute geometry with the overridden
        // material name baked into the SharpGLTF primitive.

        if (context.PlacementCount == 0 || context.WrittenParts.Count == 0)
        {
            _logError($"[GlbScene] No renderable meshes written for '{worldPackagePath}'.");
            // Even an empty render layer must still flush the lossless +
            // closure layers + manifest — they prove the actor list was
            // walked even though nothing rendered. Continue rather than
            // early-returning so the user sees the audit data on failure.
        }

        if (_options.ExportMaterials)
        {
            _log($"[GlbScene] Exporting {context.MaterialExporters.Count} materials/textures...");
            new MaterialTextureWriter(_log, _logError).Write(
                context.MaterialExporters,
                context.MaterialKeys,
                outputDirectory);
            _log("[GlbScene] Materials/textures exported.");
        }

        string outputDescription = FinalizePartsAndRecordManifest(outputBasePath, context, manifest);

        // (B) Lossless layer --------------------------------------------------
        var losslessActorList = new List<IPropertyHolder>(actors.Count);
        foreach (var placement in actors) losslessActorList.Add(placement.Actor);
        var losslessExporter = new CompleteSceneDataExporter(_log, _logError, manifest);
        losslessExporter.ExportAll(losslessActorList, actorsOutputDirectory);

        // (C) Closure layer ---------------------------------------------------
        var closureExporter = new DependencyClosureExporter(_provider, _log, _logError, manifest);
        closureExporter.ExportClosure(world.Owner, assetsOutputDirectory);

        // Manifest ------------------------------------------------------------
        try
        {
            manifest.Write(manifestPath);
            _log($"[GlbScene] Scene manifest -> {manifestPath}");
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Manifest write failed: {ex.Message}");
        }

        _log($"[GlbScene] Done. placements={context.PlacementCount} uniqueMeshes={context.UniqueMeshCount} " +
             $"parts={context.WrittenParts.Count} materials={context.MaterialCount} " +
             $"actors={manifest.Lossless.ActorCount} closure={manifest.Closure.AssetCount} " +
             $"dropped={manifest.Dropped.Actors + manifest.Dropped.Components + manifest.Dropped.Assets} " +
             $"-> {outputDescription}");
        return context.PlacementCount > 0 && context.WrittenParts.Count > 0;
    }

    // Resolve the actor's renderable components and dispatch each to the
    // first IComponentExporter that claims it. Multiple placements per actor
    // are the normal case for cooked BPs (BP_Boulder = 13 StaticMeshComponent,
    // BP_Chochin_lamp = mesh + PointLight, ...).
    private void DispatchActor(IPropertyHolder actor, FModel.Views.Snooper.Transform baseTransform, GlbSceneContext context)
    {
        int placementsBefore = context.PlacementCount;
        int claimed = 0;
        int unclaimed = 0;

        foreach (var placement in ComponentResolver.Resolve(actor, baseTransform))
        {
            bool handled = false;
            foreach (var exporter in _exporterRegistry)
            {
                if (!exporter.CanExport(placement.Component)) continue;
                exporter.Export(in placement, context);
                handled = true;
                break;
            }
            if (handled) claimed++; else unclaimed++;
        }
        _instrumentation.Resolved += claimed + unclaimed;
        _instrumentation.Claimed += claimed;
        _instrumentation.Unclaimed += unclaimed;

        // RURI_GLB_PER_ACTOR_DIAG=<ExportType>: dump per-actor claim+placement
        // counts for the named ExportType. Kept gated because emitting a line
        // per actor on a 2537-actor world would drown the run log; it is the
        // hook the parity self-test uses to compare against a known baseline.
        string actorType = (actor as UObject)?.ExportType ?? "?";
        if (Environment.GetEnvironmentVariable("RURI_GLB_PER_ACTOR_DIAG") is { } typeFilter
            && actorType.Equals(typeFilter, StringComparison.Ordinal))
        {
            string actorName = (actor as UObject)?.Name ?? "?";
            _log($"[GlbScene][PerActor] {actorType} '{actorName}': claimed={claimed} unclaimed={unclaimed} placementsAdded={context.PlacementCount - placementsBefore}");
        }
    }

    private struct InstrumentationCounters
    {
        public int Resolved;
        public int Claimed;
        public int Unclaimed;
    }
    private InstrumentationCounters _instrumentation;

    // When the whole world fit in a single part, drop the ".partNNN" suffix so
    // small maps produce a clean "<map>.glb". Returns a human-readable summary
    // of what was written and folds the part list into the manifest.
    private string FinalizePartsAndRecordManifest(string outputBasePath, GlbSceneContext context, SceneManifest manifest)
    {
        manifest.Render.PlacementCount = context.PlacementCount;
        manifest.Render.UniqueMeshCount = context.UniqueMeshCount;
        manifest.Render.MaterialCount = context.MaterialCount;

        IReadOnlyList<string> writtenParts = context.WrittenParts;
        if (writtenParts.Count == 1)
        {
            string single = outputBasePath + ".glb";
            try
            {
                if (!string.Equals(single, writtenParts[0], StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(single)) File.Delete(single);
                    File.Move(writtenParts[0], single);
                }
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Could not rename single part to '{single}': {ex.Message}");
                manifest.Render.PartFiles.Add(writtenParts[0]);
                manifest.Render.PartFileCount = 1;
                return writtenParts[0];
            }
            manifest.Render.PartFiles.Add(single);
            manifest.Render.PartFileCount = 1;
            return single;
        }

        foreach (var part in writtenParts) manifest.Render.PartFiles.Add(part);
        manifest.Render.PartFileCount = writtenParts.Count;
        return $"{writtenParts.Count} parts ('{Path.GetFileName(outputBasePath)}.partNNN.glb')";
    }

    // Map package path under the output root WITHOUT extension; the ".glb"
    // (or ".partNNN.glb") suffix is appended by the writer. Materials/textures
    // land in their own package-path-mirrored folders under the same root.
    private static string BuildOutputBase(string outputDirectory, string worldPackagePath)
    {
        string relative = worldPackagePath.Replace('\\', '/');
        if (relative.StartsWith('/')) relative = relative[1..];
        return Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    // Strips a trailing package extension (".umap"/".uasset") without touching
    // dots inside directory names.
    private static string StripExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash ? path[..dot] : path;
    }
}
