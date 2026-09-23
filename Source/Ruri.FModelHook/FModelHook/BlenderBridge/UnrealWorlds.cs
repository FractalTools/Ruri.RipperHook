using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using Ruri.RipperHook.CabMapping;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>One world the install ships: its package, whether it is partitioned, and the cells it streams.</summary>
public readonly record struct UnrealWorld(
    string Package,
    string Name,
    bool Partitioned,
    IReadOnlyList<UnrealWorldCell> Cells);

/// <summary>
/// Every world the install ships, asked of the cabmap: the packages it lists a World in, minus
/// the ones a World Partition cook generated for another world's cells.
///
/// A file extension answers nothing here -- which of ".uasset" and ".umap" a cook writes a world
/// under is that cook's choice, and a studio that writes every package under one extension still
/// ships worlds -- so the class the map already carries is what decides.
///
/// ONE reading, because a title that names its own worlds still needs the same rows underneath:
/// it joins names onto these rather than finding worlds a second way.
/// </summary>
public static class UnrealWorlds
{
    private const string WorldClassName = "World";

    /// <summary>What a cabmap row says a world package lists as -- the whole set a World produces, since any one of those ids alone is produced by other classes too.</summary>
    private static readonly int[] ClassIds =
        UnrealClasses.Of(WorldClassName, null).Select(static id => (int)id).ToArray();

    /// <summary>Every world package the map lists, alphabetical, generated cell packages left out.</summary>
    public static List<string> Packages(CabTable map)
    {
        ArgumentNullException.ThrowIfNull(map);
        string generatedMarker = "/" + UnrealWorldPartition.GeneratedFolder + "/";
        List<string> packages = new();
        for (int id = 0; id < map.Count; id++)
        {
            string cab = map.CabName(id);
            if (Holds(map, id) && !cab.Contains(generatedMarker, StringComparison.OrdinalIgnoreCase))
            {
                packages.Add(cab);
            }
        }
        packages.Sort(StringComparer.OrdinalIgnoreCase);
        return packages;
    }

    /// <summary>Every world the map lists, read: the package, its name, and its streaming cells when it is partitioned.</summary>
    public static IEnumerable<UnrealWorld> All(UnrealFileProvider provider, CabTable map)
    {
        ArgumentNullException.ThrowIfNull(provider);
        foreach (string package in Packages(map))
        {
            if (!provider.Files.TryGetValue(package, out GameFile? file)
                || provider.LoadUncached(file) is not AbstractUePackage loaded)
            {
                continue;
            }
            yield return new UnrealWorld(package, file.NameWithoutExtension,
                UnrealWorldPartition.IsPartitioned(loaded),
                UnrealWorldPartition.Cells(provider, loaded, package));
        }
    }

    private static bool Holds(CabTable map, int id)
    {
        ReadOnlySpan<int> listed = map.ClassIds(id);
        foreach (int wanted in ClassIds)
        {
            if (!listed.Contains(wanted))
            {
                return false;
            }
        }
        return ClassIds.Length > 0;
    }
}
