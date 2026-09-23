using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// One streaming cell of a World Partition world: the level package the cook wrote it to, the
/// runtime grid and hierarchical level it sits in, the world bounds of its content, and the
/// flags that decide when the game streams it.
/// </summary>
public sealed record UnrealWorldCell(
    string Name,
    string LevelPackage,
    string Grid,
    int Level,
    float LoadingRange,
    int Priority,
    FBox Bounds,
    bool AlwaysLoaded,
    bool Hlod,
    bool ClientOnlyVisible,
    IReadOnlyList<string> DataLayers);

/// <summary>
/// World Partition as the engine cooks it: a partitioned world's persistent level carries a
/// WorldPartition whose runtime hash lists every streaming cell as an object, and each cell's
/// level lives in its own package under the world's <c>_Generated_</c> folder, named after the
/// cell (WorldPartitionHelpers.cpp). Two hashes ship: the spatial hash (grids of levels of
/// layers of cells) and the hash set (runtime partitions of spatially and non-spatially loaded
/// cells); both are walked by the property names the engine declares, so a world of either
/// kind yields the same rows.
/// </summary>
public static class UnrealWorldPartition
{
    public const string GeneratedFolder = "_Generated_";

    private const string WorldPartitionName = "WorldPartition";
    private const string LevelStreamingName = "LevelStreaming";
    private const string WorldAssetName = "WorldAsset";
    private const string StreamingDataName = "StreamingData";
    private const string PackageNameName = "PackageName";
    private const string GeometryContainerName = "FastGeoContainerPtr";
    private const string RuntimeHashName = "RuntimeHash";
    private const string StreamingGridsName = "StreamingGrids";
    private const string GridNameName = "GridName";
    private const string LoadingRangeName = "LoadingRange";
    private const string GridLevelsName = "GridLevels";
    private const string LayerCellsName = "LayerCells";
    private const string GridCellsName = "GridCells";
    private const string RuntimeStreamingDataName = "RuntimeStreamingData";
    private const string PartitionName = "Name";
    private const string SpatiallyLoadedCellsName = "SpatiallyLoadedCells";
    private const string NonSpatiallyLoadedCellsName = "NonSpatiallyLoadedCells";
    private const string RuntimeCellDataName = "RuntimeCellData";
    private const string ContentBoundsName = "ContentBounds";
    private const string CellBoundsName = "CellBounds";
    private const string PriorityName = "Priority";
    private const string HierarchicalLevelName = "HierarchicalLevel";
    private const string AlwaysLoadedName = "bIsAlwaysLoaded";
    private const string HlodName = "bIsHLOD";
    private const string ClientOnlyVisibleName = "bClientOnlyVisible";
    private const string DataLayersName = "DataLayers";
    private const string DataLayerNamesName = "DataLayerNames";

    /// <summary>Whether a world's persistent level carries a WorldPartition, read from its export map alone.</summary>
    public static bool IsPartitioned(AbstractUePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return PartitionSlot(package) >= 0;
    }

    /// <summary>The export map slot of the level's WorldPartition object, or -1: the object is a subobject of the persistent level, and the level's own pointer to it is not a tagged property CUE4Parse reads.</summary>
    private static int PartitionSlot(AbstractUePackage package)
    {
        for (int slot = 0; slot < package.ExportMapLength; slot++)
        {
            if (package.ResolvePackageIndex(new FPackageIndex(package, slot + 1)) is { } header
                && string.Equals(UnrealClasses.Of(header), WorldPartitionName, StringComparison.Ordinal))
            {
                return slot;
            }
        }
        return -1;
    }

    public static IReadOnlyList<UnrealWorldCell> Cells(UnrealFileProvider provider, string worldPackagePath)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldPackagePath);
        if (provider.LoadUncached(provider[worldPackagePath]) is not AbstractUePackage package)
        {
            throw new InvalidDataException($"[Unreal] '{worldPackagePath}' is not a package with exports.");
        }
        if (PartitionSlot(package) < 0)
        {
            throw new InvalidDataException($"[Unreal] '{worldPackagePath}' is not partitioned: it carries no {WorldPartitionName}.");
        }
        return Cells(provider, package, worldPackagePath);
    }

    /// <summary>The streaming cells of a world already read, empty for a world that carries no WorldPartition.</summary>
    public static IReadOnlyList<UnrealWorldCell> Cells(UnrealFileProvider provider, AbstractUePackage package, string worldPackagePath)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldPackagePath);
        int slot = PartitionSlot(package);
        if (slot < 0)
        {
            return [];
        }
        UObject partition = package.ExportsLazy[slot].Value;
        List<UnrealWorldCell> cells = new();
        if (partition.GetOrDefault<FPackageIndex?>(RuntimeHashName)?.Load() is not { } hash)
        {
            return cells;
        }
        foreach (FStructFallback grid in hash.GetOrDefault<FStructFallback[]>(StreamingGridsName, []))
        {
            string gridName = grid.GetOrDefault<FName>(GridNameName).Text ?? string.Empty;
            float loadingRange = grid.GetOrDefault<float>(LoadingRangeName);
            FStructFallback[] levels = grid.GetOrDefault<FStructFallback[]>(GridLevelsName, []);
            for (int level = 0; level < levels.Length; level++)
            {
                foreach (FStructFallback layer in levels[level].GetOrDefault<FStructFallback[]>(LayerCellsName, []))
                {
                    foreach (FPackageIndex pointer in layer.GetOrDefault<FPackageIndex[]>(GridCellsName, []))
                    {
                        Add(cells, provider, pointer, gridName, level, loadingRange, worldPackagePath);
                    }
                }
            }
        }
        foreach (FStructFallback partitionData in hash.GetOrDefault<FStructFallback[]>(RuntimeStreamingDataName, []))
        {
            string name = partitionData.GetOrDefault<FName>(PartitionName).Text ?? string.Empty;
            float loadingRange = partitionData.GetOrDefault<int>(LoadingRangeName);
            foreach (FPackageIndex pointer in partitionData.GetOrDefault<FPackageIndex[]>(SpatiallyLoadedCellsName, []))
            {
                Add(cells, provider, pointer, name, 0, loadingRange, worldPackagePath);
            }
            foreach (FPackageIndex pointer in partitionData.GetOrDefault<FPackageIndex[]>(NonSpatiallyLoadedCellsName, []))
            {
                Add(cells, provider, pointer, name, 0, loadingRange, worldPackagePath);
            }
        }
        return cells;
    }

    /// <summary>
    /// One cell as a row. A streamed cell NAMES the level package it loads, through the level
    /// streaming object it owns, so that is where the package comes from -- reconstructing it
    /// from the world's folder and the cell's name only holds while a cook writes cells straight
    /// under the generated folder, and a cook that buckets them (by content bundle, by data
    /// layer) writes them a folder deeper. An always-loaded cell names none: the cook folds its
    /// actors into the world's persistent package (OnPrepareGeneratorPackageForCook), so that is
    /// the package its content is read from.
    /// </summary>
    /// <summary>The mount path of the level a streamed cell loads, as the cell itself names it, or null when it names none.</summary>
    private static string? LevelPackage(UnrealFileProvider provider, UObject cell)
    {
        foreach (string stated in StatedPackages(cell))
        {
            if (stated.Length > 0)
            {
                return UnrealDataTables.Key(provider, stated);
            }
        }
        return null;
    }

    /// <summary>
    /// Where a cell names the package it loads. A cell states it directly in its streaming data,
    /// or through the level streaming object it owns, or -- for a cell whose content is a
    /// geometry container rather than a level -- as that container. All three are the CELL's own
    /// statement, which is why they are asked in turn and the first answer taken: reconstructing
    /// the name instead, out of the world's folder and the cell's own name, is a guess about
    /// where a cook writes its output and breaks the moment a cook buckets its cells.
    /// </summary>
    private static IEnumerable<string> StatedPackages(UObject cell)
    {
        if (cell.GetOrDefault<FStructFallback?>(StreamingDataName) is { } streaming)
        {
            yield return streaming.GetOrDefault(PackageNameName, string.Empty);
            if (streaming.GetOrDefault<FSoftObjectPath?>(WorldAssetName) is { } stated)
            {
                yield return stated.AssetPathName.Text ?? string.Empty;
            }
        }
        if (cell.GetOrDefault<FPackageIndex?>(LevelStreamingName)?.Load() is { } level
            && level.GetOrDefault<FSoftObjectPath?>(WorldAssetName) is { } asset)
        {
            yield return asset.AssetPathName.Text ?? string.Empty;
        }
        if (cell.GetOrDefault<FSoftObjectPath?>(GeometryContainerName) is { } container)
        {
            yield return container.AssetPathName.Text ?? string.Empty;
        }
    }

    private static void Add(List<UnrealWorldCell> cells, UnrealFileProvider provider, FPackageIndex pointer, string grid, int level, float loadingRange, string worldPackagePath)
    {
        if (pointer.Load() is not { } cell)
        {
            return;
        }
        UObject? data = cell.GetOrDefault<FPackageIndex?>(RuntimeCellDataName)?.Load();
        FBox bounds = default;
        int priority = 0;
        int hierarchicalLevel = level;
        if (data is not null)
        {
            bounds = data.GetOrDefault<FBox>(CellBoundsName);
            if (bounds.IsValid == 0)
            {
                bounds = data.GetOrDefault<FBox>(ContentBoundsName);
            }
            priority = data.GetOrDefault<int>(PriorityName);
            hierarchicalLevel = data.GetOrDefault<int>(HierarchicalLevelName, level);
        }
        List<string> dataLayers = new();
        if (cell.GetOrDefault<FStructFallback?>(DataLayersName) is { } layers)
        {
            foreach (FName layer in layers.GetOrDefault<FName[]>(DataLayerNamesName, []))
            {
                if (layer.Text is { Length: > 0 } text)
                {
                    dataLayers.Add(text);
                }
            }
        }
        bool alwaysLoaded = cell.GetOrDefault<bool>(AlwaysLoadedName);
        cells.Add(new UnrealWorldCell(
            cell.Name,
            alwaysLoaded ? worldPackagePath : LevelPackage(provider, cell) ?? worldPackagePath,
            grid,
            hierarchicalLevel,
            loadingRange,
            priority,
            bounds,
            alwaysLoaded,
            cell.GetOrDefault<bool>(HlodName),
            cell.GetOrDefault<bool>(ClientOnlyVisibleName),
            dataLayers));
    }
}
