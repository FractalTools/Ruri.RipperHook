using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.GUI.Services;

/// <summary>
/// GUI façade over <see cref="CabMap"/> — the same cabmap reader/writer the CLI and the Blender pythonnet
/// bridge use (RCM4, columnar, self-contained: names inline, no sidecar). A map built by any of the three
/// producers loads identically in the other two. Build it once over the whole game (one file at a time, low
/// peak memory), then resolve exactly which on-disk files to load for a given target — by asset type
/// (<see cref="ResolveFilesByTypes"/>) or by a seed CAB's dependency closure
/// (<see cref="ResolveScopedClosure"/>) — instead of loading the whole game into memory and filtering.
/// </summary>
internal sealed class ExportCabMap
{
    private Dictionary<string, CabMap.Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private string _baseFolder = string.Empty;

    public bool HasMap => _entries.Count > 0;
    public bool HasNames => CabMap.HasInlineNames(_entries);
    public int CabCount => _entries.Count;
    public string MapPath { get; private set; } = string.Empty;

    /// <summary>One virtual-file row for the browser: a CAB, where it lives, what it holds, how connected it is.</summary>
    internal sealed record CabRow(string Cab, string RelativePath, IReadOnlyList<int> ClassIds, int DependencyCount, IReadOnlyList<string> ContainerPaths);

    /// <summary>All distinct ClassIDs present anywhere in the map — used to populate the type picker.</summary>
    public IReadOnlySet<int> AvailableClassIds
    {
        get
        {
            HashSet<int> ids = new();
            foreach (CabMap.Entry e in _entries.Values)
            {
                foreach (int c in e.ClassIds) ids.Add(c);
            }
            return ids;
        }
    }

    public void Clear()
    {
        _entries = new(StringComparer.OrdinalIgnoreCase);
        _baseFolder = string.Empty;
        MapPath = string.Empty;
    }

    /// <summary>Every CAB as a virtual-file row (with its Container paths — RCM4 always carries them inline).</summary>
    public IEnumerable<CabRow> EnumerateCabRows()
    {
        foreach ((string cab, CabMap.Entry entry) in _entries)
        {
            yield return new CabRow(cab, entry.RelativePath, entry.ClassIds, entry.Dependencies.Count, entry.ContainerPaths);
        }
    }

    /// <summary>
    /// Resolve the given seed CABs to a scoped, bundle-granular load: the on-disk chunk files that host them
    /// plus their transitive dependency closure, AND the chunk-ENTRY file names of every CAB in the closure.
    /// The caller hands the file names to <c>GameBundleHook.LoadIncludeFile</c> so only those bundles are
    /// extracted from the (possibly 161k-bundle) chunks instead of loading each chunk whole.
    /// </summary>
    public (string[] Files, HashSet<string> LoadFilterFileNames) ResolveScopedClosure(IEnumerable<string> seedCabs)
        => CabMap.ResolveScopedClosure(_baseFolder, _entries, seedCabs);

    public void Load(string path)
    {
        (_baseFolder, _entries) = CabMap.Load(path);
        MapPath = Path.GetFullPath(path);
    }

    /// <summary>On-disk files hosting a CAB that contains any of <paramref name="targetClassIds"/>, plus their deps.</summary>
    public string[] ResolveFilesByTypes(IReadOnlySet<int> targetClassIds)
        => CabMap.ResolveByTypes(_baseFolder, _entries, targetClassIds, out _);

    /// <summary>Build a self-contained (RCM4) map over <paramref name="rootFolder"/> and write it to <paramref name="outPath"/>.
    /// The caller must already have the right game hook applied so encrypted bundles load. Returns the number of CABs indexed.</summary>
    public static int Build(string rootFolder, string outPath) => CabMap.Build(rootFolder, outPath);
}
