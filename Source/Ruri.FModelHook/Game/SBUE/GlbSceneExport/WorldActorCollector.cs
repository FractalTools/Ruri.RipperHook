using System;
using System.Collections.Generic;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.WorldPartition;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// One collected placement: an actor (or component-holder) plus the base
// transform of the world it belongs to. WP cells / streaming sub-levels are
// authored in world space, so the base is Identity for everything today —
// the field exists so level-instance offsets can be threaded through later
// without reshaping the API.
internal readonly struct WorldActor
{
    public readonly IPropertyHolder Actor;
    public readonly Transform BaseTransform;

    public WorldActor(IPropertyHolder actor, Transform baseTransform)
    {
        Actor = actor;
        BaseTransform = baseTransform;
    }
}

// Resolves the COMPLETE set of placed actors for a UWorld — the piece FModel's
// own world preview does not do. FModel's Snooper only walks the actors cooked
// directly into the opened .umap (Renderer.cs:443-456); a UE5 World Partition
// map keeps almost all of its content in separate packages (baked streaming
// cells, or One-File-Per-Actor source assets), so the top-level map looks empty.
//
// This collector aggregates every source so the exported scene is whole:
//   1. Actors embedded in the persistent level            (Renderer parity).
//   2. World Partition runtime-hash cells                 (cooked open worlds):
//        WorldSettings -> WorldPartition -> RuntimeHash -> cells ->
//        LevelStreaming.WorldAsset -> cell UWorld -> its actors.
//      Both runtime-hash shapes are handled: UWorldPartitionRuntimeSpatialHash
//      (StreamingGrids -> GridLevels -> LayerCells -> GridCells) and
//      UWorldPartitionRuntimeHashSet (RuntimeStreamingData -> {Spatially,
//      NonSpatially}LoadedCells).
//   3. Generated cell maps discovered by package path      (cooked safety net):
//        any *.umap under "<MainMap>/..." namespace.
//   4. Streaming sub-levels (UWorld.StreamingLevels).
//   5. One-File-Per-Actor external actors under "<mount>/Content/__ExternalActors__/...".
//
// Worlds are de-duplicated by package name so a cell reached through several
// sources is only walked once. Dispatch is by data (hash type / package path),
// never by per-game branches.
internal sealed class WorldActorCollector
{
    private readonly IFileProvider _provider;
    private readonly Action<string> _log;
    private readonly Action<string> _logError;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<string> _visitedWorlds = new(StringComparer.OrdinalIgnoreCase);

    public int EmbeddedActorCount { get; private set; }
    public int CellWorldCount { get; private set; }
    public int GeneratedCellCount { get; private set; }
    public int StreamingLevelCount { get; private set; }
    public int ExternalActorCount { get; private set; }

    public WorldActorCollector(IFileProvider provider, Action<string> log, Action<string> logError, CancellationToken cancellationToken)
    {
        _provider = provider;
        _log = log;
        _logError = logError;
        _cancellationToken = cancellationToken;
    }

    public List<WorldActor> Collect(UWorld mainWorld, string mainWorldPackagePath)
    {
        var result = new List<WorldActor>();
        var worldQueue = new Queue<UWorld>();
        worldQueue.Enqueue(mainWorld);

        // Pre-scan the provider once, bucketing the two path-derived sources
        // (generated cell maps + OFPA external actors) so we touch Files only
        // a single time even for large games.
        ScanProviderFiles(mainWorldPackagePath, out var generatedCellKeys, out var externalActorKeys);

        foreach (var key in generatedCellKeys)
        {
            if (TryLoadWorld(key, out var cellWorld))
            {
                GeneratedCellCount++;
                worldQueue.Enqueue(cellWorld);
            }
        }

        while (worldQueue.Count > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var world = worldQueue.Dequeue();

            string worldKey = world.Owner?.Name ?? world.GetPathName();
            if (!_visitedWorlds.Add(worldKey)) continue;

            if (world.PersistentLevel?.Load<ULevel>() is not { } level) continue;

            CollectEmbeddedActors(level, result);

            foreach (var cellWorld in EnumerateWorldPartitionCellWorlds(level))
            {
                worldQueue.Enqueue(cellWorld);
            }

            foreach (var subWorld in EnumerateStreamingLevelWorlds(world))
            {
                worldQueue.Enqueue(subWorld);
            }
        }

        CollectExternalActors(externalActorKeys, result);

        _log($"[GlbScene] Actor sources: embedded={EmbeddedActorCount} wpCellWorlds={CellWorldCount} " +
             $"generatedCellWorlds={GeneratedCellCount} streamingLevels={StreamingLevelCount} externalActors={ExternalActorCount} " +
             $"(worlds visited={_visitedWorlds.Count}, total placements={result.Count})");
        return result;
    }

    private void CollectEmbeddedActors(ULevel level, List<WorldActor> result)
    {
        if (level.Actors == null) return;
        foreach (var actorIndex in level.Actors)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (actorIndex == null || actorIndex.IsNull) continue;
            if (actorIndex.Load() is not { } actor) continue;
            // HLOD proxy actors duplicate real geometry at lower detail; skip
            // them exactly as the FModel preview does (Renderer.cs:448).
            if (actor.ExportType == "LODActor") continue;
            result.Add(new WorldActor(actor, Transform.Identity));
            EmbeddedActorCount++;
        }
    }

    private IEnumerable<UWorld> EnumerateWorldPartitionCellWorlds(ULevel level)
    {
        UWorldPartition? worldPartition = null;
        try
        {
            if (level.WorldSettings != null && !level.WorldSettings.IsNull &&
                level.WorldSettings.Load() is { } worldSettings &&
                worldSettings.GetOrDefault<FPackageIndex>("WorldPartition") is { IsNull: false } wpIndex)
            {
                worldPartition = wpIndex.Load<UWorldPartition>();
            }
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Failed to resolve WorldPartition: {ex.Message}");
        }

        UObject? runtimeHash = null;
        try
        {
            runtimeHash = worldPartition?.RuntimeHash is { IsNull: false } hashIndex ? hashIndex.Load() : null;
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Failed to load WorldPartition RuntimeHash: {ex.Message}");
        }
        if (runtimeHash != null)
        {
            _log($"[GlbScene] WorldPartition runtime hash: {runtimeHash.ExportType}");
        }

        foreach (var cell in EnumerateCells(runtimeHash))
        {
            if (cell is not UWorldPartitionRuntimeLevelStreamingCell streamingCell) continue;
            FPackageIndex? levelStreamingIndex = streamingCell.LevelStreaming;
            if (levelStreamingIndex == null || levelStreamingIndex.IsNull) continue;

            UWorld? cellWorld = null;
            try
            {
                if (levelStreamingIndex.Load<ULevelStreaming>() is { WorldAsset: { } worldAsset } &&
                    worldAsset.TryLoad<UWorld>(out var loaded))
                {
                    cellWorld = loaded;
                }
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Failed to load cell world: {ex.Message}");
            }

            if (cellWorld != null)
            {
                CellWorldCount++;
                yield return cellWorld;
            }
        }
    }

    private IEnumerable<UObject> EnumerateCells(UObject? runtimeHash)
    {
        switch (runtimeHash)
        {
            case UWorldPartitionRuntimeSpatialHash spatialHash:
                foreach (var grid in spatialHash.StreamingGrids ?? [])
                foreach (var gridLevel in grid.GridLevels ?? [])
                foreach (var layerCell in gridLevel.LayerCells ?? [])
                foreach (var cellIndex in layerCell.GridCells ?? [])
                {
                    if (cellIndex is { IsNull: false } && cellIndex.Load() is { } cell)
                        yield return cell;
                }
                break;

            case UWorldPartitionRuntimeHashSet hashSet:
                foreach (var data in hashSet.RuntimeStreamingData ?? [])
                {
                    foreach (var cell in LoadCells(data.SpatiallyLoadedCells)) yield return cell;
                    foreach (var cell in LoadCells(data.NonSpatiallyLoadedCells)) yield return cell;
                }
                break;
        }
    }

    private static IEnumerable<UObject> LoadCells(FPackageIndex[]? cells)
    {
        foreach (var cellIndex in cells ?? [])
        {
            if (cellIndex is { IsNull: false } && cellIndex.Load() is { } cell)
                yield return cell;
        }
    }

    private IEnumerable<UWorld> EnumerateStreamingLevelWorlds(UWorld world)
    {
        foreach (var streamingIndex in world.StreamingLevels ?? [])
        {
            if (streamingIndex is not { IsNull: false }) continue;

            UWorld? subWorld = null;
            try
            {
                if (streamingIndex.Load<ULevelStreaming>() is { WorldAsset: { } worldAsset } &&
                    worldAsset.TryLoad<UWorld>(out var loaded))
                {
                    subWorld = loaded;
                }
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Failed to load streaming level world: {ex.Message}");
            }

            if (subWorld != null)
            {
                StreamingLevelCount++;
                yield return subWorld;
            }
        }
    }

    private void CollectExternalActors(List<string> externalActorKeys, List<WorldActor> result)
    {
        foreach (var key in externalActorKeys)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!TryLoadPackage(key, out var package)) continue;
            foreach (var export in package.GetExports())
            {
                if (export is AActor actor)
                {
                    result.Add(new WorldActor(actor, Transform.Identity));
                    ExternalActorCount++;
                }
            }
        }
    }

    // Single pass over the provider file table. Generated cell maps are *.umap
    // packages nested under the main map's package namespace; external actors
    // are *.uasset packages under the matching "__ExternalActors__" prefix.
    private void ScanProviderFiles(string mainWorldPackagePath, out List<string> generatedCellKeys, out List<string> externalActorKeys)
    {
        generatedCellKeys = new List<string>();
        externalActorKeys = new List<string>();

        string generatedCellPrefix = mainWorldPackagePath + "/";
        string? externalActorPrefix = BuildExternalActorPrefix(mainWorldPackagePath);

        foreach (var key in _provider.Files.Keys)
        {
            if (key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                if (key.StartsWith(generatedCellPrefix, StringComparison.OrdinalIgnoreCase))
                    generatedCellKeys.Add(key);
            }
            else if (externalActorPrefix != null &&
                     key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                     key.StartsWith(externalActorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                externalActorKeys.Add(key);
            }
        }
    }

    // "<mount>/Content/Maps/MyMap" -> "<mount>/Content/__ExternalActors__/Maps/MyMap/".
    // Mirrors Unreal's OFPA on-disk layout (the "/Game/Maps/MyMap" logical path
    // becomes "/Game/__ExternalActors__/Maps/MyMap", and "/Game" maps to
    // "<mount>/Content" in provider paths).
    private static string? BuildExternalActorPrefix(string mainWorldPackagePath)
    {
        const string contentSegment = "/Content/";
        int index = mainWorldPackagePath.IndexOf(contentSegment, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        string head = mainWorldPackagePath[..(index + "/Content".Length)];
        string rest = mainWorldPackagePath[(index + contentSegment.Length)..];
        return $"{head}/__ExternalActors__/{rest}/";
    }

    private bool TryLoadWorld(string packagePath, out UWorld world)
    {
        world = null!;
        if (!TryLoadPackage(packagePath, out var package)) return false;
        foreach (var export in package.GetExports())
        {
            if (export is UWorld loaded)
            {
                world = loaded;
                return true;
            }
        }
        return false;
    }

    private bool TryLoadPackage(string key, out IPackage package)
    {
        package = null!;
        try
        {
            if (!_provider.Files.TryGetValue(key, out var gameFile)) return false;
            package = _provider.LoadPackage(gameFile);
            return package != null;
        }
        catch (Exception ex)
        {
            _logError($"[GlbScene] Failed to load package '{key}': {ex.Message}");
            return false;
        }
    }
}
