using AssetRipper.Assets;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>Loading the dependency closure of a set of seed CABs -- the ONE statement of what
/// "everything this reaches" means and of how the load is gated to it, so the importer, the
/// shader reader and every dataset that asks about a selection all see the same assets.</summary>
public static class ClosureReader
{
    public static CabClosure Resolve(CabTable table, IEnumerable<string> seedCabNames,
        bool reachThroughDependents = false, bool scriptedDependenciesOnly = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(seedCabNames);
        return new CabSelection
        {
            SeedCabNames = seedCabNames.ToArray(),
            ReachThroughDependents = reachThroughDependents,
            ScriptedDependenciesOnly = scriptedDependenciesOnly,
        }.Resolve(table);
    }

    /// <summary>Load one closure's files, gated to the closure itself. The gate is process-wide
    /// state on the bundle hook, so it is set and cleared HERE rather than by each caller
    /// remembering to.</summary>
    public static GameData Load(CabClosure closure, ExportHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        HashSet<string> loadFilter = closure.LoadFilterFileNames;
        GameBundleHook.LoadIncludeFile = loadFilter.Count > 0 ? name => loadFilter.Contains(name) : null;
        HashSet<string> seedFiles = closure.SeedFileNames;
        GameBundleHook.LoadSeedFile = seedFiles.Count > 0 ? name => seedFiles.Contains(name) : null;
        GameBundleHook.LoadMappedFile = closure.Mapped;
        try
        {
            return handler.Load(closure.Files, LocalFileSystem.Instance);
        }
        finally
        {
            GameBundleHook.LoadIncludeFile = null;
            GameBundleHook.LoadSeedFile = null;
            GameBundleHook.LoadMappedFile = null;
        }
    }

    /// <summary>What a reader of a closure asks for: the assets, under the settings a read needs
    /// and nothing an export would. Null when the seeds are in no loaded map -- a caller that has
    /// nothing to say about an empty selection says nothing.</summary>
    public static GameData? Read(CabTable table, IEnumerable<string> seedCabNames,
        bool reachThroughDependents = false)
    {
        CabClosure closure = Resolve(table, seedCabNames, reachThroughDependents);
        if (closure.Files.Length == 0)
        {
            return null;
        }
        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
        return Load(closure, new ExportHandler(settings));
    }

    /// <summary>The closure's assets, for a caller to whom an empty selection is a mistake.</summary>
    public static GameData Load(CabTable table, IEnumerable<string> seedCabNames) =>
        Read(table, seedCabNames) ?? throw new InvalidOperationException(
            "no files resolved for the requested CABs -- they are not in this cabmap.");

    /// <summary>Every asset the closure of these archives holds. Naming no archive is a selection with
    /// nothing in it -- a level that ships no decal, a stage with no cookie -- and reads nothing; naming
    /// archives the map does not hold is still the mistake <see cref="Load(CabTable, IEnumerable{string})"/>
    /// refuses.</summary>
    public static IEnumerable<IUnityObjectBase> Assets(CabTable table, IReadOnlyCollection<string> seedCabNames) =>
        seedCabNames.Count == 0 ? [] : Load(table, seedCabNames).GameBundle.FetchAssets();

    /// <summary>The one <typeparamref name="T"/> each container path files: found in the archive the map files that
    /// path in -- never elsewhere in its closure, where an engine default of the same name sits beside it -- and,
    /// where that archive holds more than one, the one named as the path names it: the sub-asset after <c>##</c>,
    /// else the file's own stem. An archive holding exactly one is answered by it whatever it is called now: an
    /// asset renamed after it was filed keeps its old path. <paramref name="kind"/> is the sort of
    /// <typeparamref name="T"/> the caller reads, where an archive files it with others of the same class (a
    /// volume profile with the components it owns).</summary>
    public static Dictionary<string, T> AssetsAt<T>(CabTable table, IReadOnlyCollection<string> paths,
        Func<T, bool>? kind = null)
        where T : class, IUnityObjectBase
    {
        Dictionary<string, string> cabOf = CabMap.CabsOf(table, paths);
        HashSet<string> wanted = new(cabOf.Values, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<T>> held = new(StringComparer.OrdinalIgnoreCase);
        foreach (IUnityObjectBase asset in Assets(table, wanted.ToArray()))
        {
            if (asset is T typed && (kind is null || kind(typed)) && wanted.Contains(asset.Collection.Name))
            {
                if (!held.TryGetValue(asset.Collection.Name, out List<T>? inArchive))
                {
                    held[asset.Collection.Name] = inArchive = [];
                }
                inArchive.Add(typed);
            }
        }
        Dictionary<string, T> found = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string cab) in cabOf)
        {
            List<T> inArchive = held.GetValueOrDefault(cab) ?? [];
            string name = NameIn(path);
            T[] named = inArchive.Count == 1
                ? [inArchive[0]]
                : inArchive.Where(asset => string.Equals(asset.GetBestName(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            found[path] = named.Length == 1
                ? named[0]
                : throw new InvalidDataException($"'{path}' resolves to {named.Length} {typeof(T).Name}(s) named "
                    + $"'{name}' among the {inArchive.Count} its archive {cab} holds.");
        }
        return found;
    }

    /// <summary>The name a container path gives its asset: the sub-asset after <c>##</c>, else the file's stem.</summary>
    private static string NameIn(string path)
    {
        int marker = path.IndexOf("##", StringComparison.Ordinal);
        return marker >= 0 ? path[(marker + 2)..] : Path.GetFileNameWithoutExtension(path);
    }
}
