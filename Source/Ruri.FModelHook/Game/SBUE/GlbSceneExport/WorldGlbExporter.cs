using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion.Options;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class WorldGlbExporter
{
    private static IComponentExporter[] BuildExporterRegistry() => new IComponentExporter[]
    {
        new SplineMeshComponentExporter(),
        new StaticMeshComponentExporter(),
        new LightComponentExporter(),
        new CameraComponentExporter(),
        new LandscapeComponentExporter(),
    };

    private readonly IFileProvider _provider;
    private readonly ExportOptions _options;
    private readonly Action<string> _log;
    private readonly Action<string> _logError;
    private readonly IComponentExporter[] _exporterRegistry;

    public WorldGlbExporter(IFileProvider provider, ExportOptions options, Action<string> log, Action<string> logError)
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
        string scanKey = StripExtension(sourcePackageKey);
        _log($"[GlbScene] Exporting world '{worldPackagePath}' (file key '{scanKey}') ...");

        string outputBasePath = BuildOutputBase(outputDirectory, worldPackagePath);
        string actorsOutputDirectory = outputBasePath + "_Actors";
        string assetsOutputDirectory = outputBasePath + "_Assets";
        string manifestPath = outputBasePath + ".scene-manifest.json";

        var collector = new WorldActorCollector(_provider, _log, _logError, cancellationToken);
        List<WorldActor> actors = collector.Collect(world, scanKey);
        SceneCensus.Log(actors, _log);

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

        var manifest = new SceneManifest
        {
            SourceMapPackagePath = worldPackagePath,
            GameVersion = _provider.Versions?.Game.ToString() ?? string.Empty,
        };
        var materialFactory = new GlbMaterialFactory(_log, _logError);
        var context = new GlbSceneContext(_provider, _options, _log, _logError, materialFactory, manifest);
        context.SetOutputBasePath(outputBasePath);

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
        context.WritePendingLightsAndCameras();
        _log($"[GlbScene][Diag] resolver yielded {_instrumentation.Resolved} components (claimed={_instrumentation.Claimed}, unclaimed={_instrumentation.Unclaimed}); pendingLights={context.PendingLightCount} pendingCameras={context.PendingCameraCount}.");


        if (context.PlacementCount == 0 || context.WrittenParts.Count == 0)
        {
            _logError($"[GlbScene] No renderable meshes written for '{worldPackagePath}'.");
        }

        if (_options.ExportMaterials)
        {
            _log($"[GlbScene] Exporting {context.Materials.Count} materials/textures...");
            new MaterialTextureWriter(_log, _logError).Write(
                context.Materials,
                context.MaterialKeys,
                _options,
                outputDirectory);
            _log("[GlbScene] Materials/textures exported.");
        }

        string outputDescription = FinalizePartsAndRecordManifest(outputBasePath, context, manifest);

        var losslessActorList = new List<IPropertyHolder>(actors.Count);
        foreach (var placement in actors) losslessActorList.Add(placement.Actor);
        var losslessExporter = new CompleteSceneDataExporter(_log, _logError, manifest);
        losslessExporter.ExportAll(losslessActorList, actorsOutputDirectory);

        var closureExporter = new DependencyClosureExporter(_provider, _log, _logError, manifest);
        closureExporter.ExportClosure(world.Owner, assetsOutputDirectory);

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

    private static string BuildOutputBase(string outputDirectory, string worldPackagePath)
    {
        string relative = worldPackagePath.Replace('\\', '/');
        if (relative.StartsWith('/')) relative = relative[1..];
        return Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StripExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash ? path[..dot] : path;
    }
}
