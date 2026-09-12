using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Unreal;

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
    public const string WorldExtension = ".umap";

    private const string WorldPartitionName = "WorldPartition";
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

    /// <summary>Whether the package is a world whose persistent level carries a WorldPartition, read from its export map alone.</summary>
    public static bool IsPartitioned(UnrealFileProvider provider, GameFile file)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(file);
        return provider.LoadUncached(file) is AbstractUePackage package && PartitionSlot(package) >= 0;
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

    /// <summary>The generated level packages of a world's cells live beside it, in a folder named after the world.</summary>
    public static string GeneratedRoot(string worldPackagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldPackagePath);
        string trimmed = worldPackagePath.Replace('\\', '/');
        int dot = trimmed.LastIndexOf('.');
        int slash = trimmed.LastIndexOf('/');
        string stem = dot > slash ? trimmed[..dot] : trimmed;
        return stem + "/" + GeneratedFolder + "/";
    }

    public static IReadOnlyList<UnrealWorldCell> Cells(UnrealFileProvider provider, string worldPackagePath)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldPackagePath);
        if (provider.LoadUncached(provider[worldPackagePath]) is not AbstractUePackage package)
        {
            throw new InvalidDataException($"[Unreal] '{worldPackagePath}' is not a package with exports.");
        }
        int slot = PartitionSlot(package);
        if (slot < 0)
        {
            throw new InvalidDataException($"[Unreal] '{worldPackagePath}' is not partitioned: it carries no {WorldPartitionName}.");
        }
        UObject partition = package.ExportsLazy[slot].Value;
        string generatedRoot = GeneratedRoot(worldPackagePath);
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
                        Add(cells, pointer, gridName, level, loadingRange, generatedRoot, worldPackagePath);
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
                Add(cells, pointer, name, 0, loadingRange, generatedRoot, worldPackagePath);
            }
            foreach (FPackageIndex pointer in partitionData.GetOrDefault<FPackageIndex[]>(NonSpatiallyLoadedCellsName, []))
            {
                Add(cells, pointer, name, 0, loadingRange, generatedRoot, worldPackagePath);
            }
        }
        return cells;
    }

    /// <summary>
    /// One cell as a row. An always-loaded cell has no package of its own: the cook folds its
    /// actors into the world's persistent package (OnPrepareGeneratorPackageForCook), so that is
    /// the package its content is read from.
    /// </summary>
    private static void Add(List<UnrealWorldCell> cells, FPackageIndex pointer, string grid, int level, float loadingRange, string generatedRoot, string worldPackagePath)
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
            alwaysLoaded ? worldPackagePath : generatedRoot + cell.Name + WorldExtension,
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
