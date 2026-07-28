using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.GUI.Services;

/// <summary>
/// GUI façade over <see cref="CabMap"/> — the same cabmap reader/writer the CLI and the Blender pythonnet
/// bridge use (RCM6, columnar, self-contained: names inline, no sidecar, location-independent). A map built by any of the three
/// producers loads identically in the other two. Build it once over the whole game (parallel, bounded
/// memory), then resolve exactly which on-disk files to load for a given target — every resolve goes
/// through <see cref="CabSelection"/> on the columnar int graph, never a materialized dictionary.
/// </summary>
internal sealed class ExportCabMap
{
    private CabTable? _table;

    public bool HasMap => _table is not null;
    public int CabCount => _table?.Count ?? 0;
    public string MapPath { get; private set; } = string.Empty;

    /// <summary>One virtual-file row for the browser: a CAB, where it lives, what it holds, how connected it is.</summary>
    internal sealed record CabRow(string Cab, string RelativePath, IReadOnlyList<int> ClassIds, int DependencyCount, IReadOnlyList<string> ContainerPaths);

    /// <summary>All distinct ClassIDs present anywhere in the map — used to populate the type picker.</summary>
    public IReadOnlySet<int> AvailableClassIds
    {
        get
        {
            HashSet<int> ids = new();
            if (_table is { } table)
            {
                for (int id = 0; id < table.Count; id++)
                {
                    foreach (int classId in table.ClassIds(id))
                    {
                        ids.Add(classId);
                    }
                }
            }
            return ids;
        }
    }

    public void Clear()
    {
        _table = null;
        MapPath = string.Empty;
    }

    /// <summary>Every CAB as a virtual-file row (with its Container paths — the map always carries them inline).</summary>
    public IEnumerable<CabRow> EnumerateCabRows()
    {
        if (_table is not { } table)
        {
            yield break;
        }
        for (int id = 0; id < table.Count; id++)
        {
            int pathCount = table.ContainerPathCount(id);
            string[] paths = new string[pathCount];
            for (int i = 0; i < pathCount; i++)
            {
                paths[i] = table.ContainerPath(id, i);
            }
            yield return new CabRow(
                table.CabName(id), table.RelativePath(id), table.ClassIds(id).ToArray(),
                table.DependencyCount(id), paths);
        }
    }

    /// <summary>
    /// Resolve the given seed CABs to a scoped, bundle-granular load: the on-disk chunk files that host them
    /// plus their transitive dependency closure, AND the chunk-ENTRY file names of every CAB in the closure.
    /// The caller hands the file names to <c>GameBundleHook.LoadIncludeFile</c> so only those bundles are
    /// extracted from the (possibly 161k-bundle) chunks instead of loading each chunk whole.
    /// </summary>
    public (string[] Files, HashSet<string> LoadFilterFileNames) ResolveScopedClosure(IEnumerable<string> seedCabs)
    {
        if (_table is not { } table)
        {
            return ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        CabClosure closure = new CabSelection { SeedCabNames = seedCabs.ToArray() }.Resolve(table);
        return (closure.Files, closure.LoadFilterFileNames);
    }

    public void Load(string path)
    {
        _table = CabMap.LoadTable(path);
        MapPath = Path.GetFullPath(path);
    }

    /// <summary>On-disk files hosting a CAB that contains any of <paramref name="targetClassIds"/>, plus their deps.</summary>
    public string[] ResolveFilesByTypes(IReadOnlySet<int> targetClassIds)
        => _table is { } table
            ? new CabSelection { ClassIds = targetClassIds }.Resolve(table).Files
            : [];

    /// <summary>Build a self-contained (RCM6) map over <paramref name="rootFolder"/> and write it to <paramref name="outPath"/>.
    /// The caller must already have the right game hook applied so encrypted bundles load. Returns the number of CABs indexed.</summary>
    public static int Build(string rootFolder, string outPath) => CabMap.Build(rootFolder, outPath);
}
