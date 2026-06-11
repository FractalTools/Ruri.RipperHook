using System.Text;
using AssetRipper.Assets;
using AssetRipper.GUI.Web;

namespace Ruri.RipperHook.GUI.Services;

/// <summary>
/// Typed CABMap held by the GUI for map-aware exports: CAB → (relative path, dependencies, ClassIDs).
/// Mirrors the CLI's <c>CabMap</c> format (magic + version header). Build it once over the whole game
/// (one file at a time, low peak memory), then resolve exactly which on-disk files to load for a given
/// target — by asset type (<see cref="ResolveFilesByTypes"/>) or by a seed asset's dependency closure
/// (<see cref="ResolveFilesWithDeps"/>) — instead of loading the whole game into memory and filtering.
/// </summary>
internal sealed class ExportCabMap
{
    private const uint Magic = 0x52434D32; // "RCM2" — keep in sync with Ruri.RipperHook.CLI CabMap

    private sealed record Entry(string RelativePath, List<string> Dependencies, List<int> ClassIds);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pathToCabs = new(StringComparer.OrdinalIgnoreCase);
    private string _baseFolder = string.Empty;

    public bool HasMap => _entries.Count > 0;
    public int CabCount => _entries.Count;
    public string MapPath { get; private set; } = string.Empty;

    /// <summary>All distinct ClassIDs present anywhere in the map — used to populate the type picker.</summary>
    public IReadOnlySet<int> AvailableClassIds
    {
        get
        {
            HashSet<int> ids = new();
            foreach (Entry e in _entries.Values)
            {
                foreach (int c in e.ClassIds) ids.Add(c);
            }
            return ids;
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _pathToCabs.Clear();
        _baseFolder = string.Empty;
        MapPath = string.Empty;
    }

    public void Load(string path)
    {
        Clear();
        string mapDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

        bool typed = stream.Length >= 4 && reader.ReadUInt32() == Magic;
        if (typed)
        {
            reader.ReadInt32(); // version
        }
        else
        {
            stream.Position = 0; // legacy headerless format (no ClassIDs)
        }

        string storedBase = reader.ReadString();
        _baseFolder = Path.GetFullPath(Path.Combine(mapDir, storedBase));

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string cab = reader.ReadString();
            string relativePath = reader.ReadString();
            reader.ReadInt64(); // offset (unused here)
            int depCount = reader.ReadInt32();
            List<string> deps = new(depCount);
            for (int j = 0; j < depCount; j++) deps.Add(reader.ReadString());

            List<int> classIds;
            if (typed)
            {
                int cc = reader.ReadInt32();
                classIds = new List<int>(cc);
                for (int j = 0; j < cc; j++) classIds.Add(reader.ReadInt32());
            }
            else
            {
                classIds = [];
            }
            _entries[cab] = new Entry(relativePath, deps, classIds);
        }

        foreach ((string cab, Entry e) in _entries)
        {
            if (!_pathToCabs.TryGetValue(e.RelativePath, out List<string>? list))
            {
                list = [];
                _pathToCabs[e.RelativePath] = list;
            }
            list.Add(cab);
        }
        MapPath = Path.GetFullPath(path);
    }

    /// <summary>On-disk files hosting a CAB that contains any of <paramref name="targetClassIds"/>, plus their deps.</summary>
    public string[] ResolveFilesByTypes(IReadOnlySet<int> targetClassIds)
    {
        Queue<string> seeds = new();
        foreach ((string cab, Entry e) in _entries)
        {
            if (e.ClassIds.Any(targetClassIds.Contains)) seeds.Enqueue(cab);
        }
        return Bfs(seeds);
    }

    /// <summary>On-disk files for the given CABs plus their transitive dependency closure.</summary>
    public string[] ResolveFilesByCabs(IEnumerable<string> cabNames)
    {
        Queue<string> seeds = new();
        foreach (string cab in cabNames)
        {
            if (!string.IsNullOrWhiteSpace(cab)) seeds.Enqueue(cab);
        }
        return Bfs(seeds);
    }

    /// <summary>On-disk files for the seed files' CABs plus their transitive dependency closure.</summary>
    public string[] ResolveFilesWithDeps(IEnumerable<string> seedFiles)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> seeds = new();
        foreach (string raw in seedFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full = Path.GetFullPath(raw);
            result.Add(full);
            string relative = string.IsNullOrWhiteSpace(_baseFolder) ? Path.GetFileName(full) : Path.GetRelativePath(_baseFolder, full);
            if (_pathToCabs.TryGetValue(relative, out List<string>? cabs))
            {
                foreach (string cab in cabs) seeds.Enqueue(cab);
            }
        }
        foreach (string f in Bfs(seeds)) result.Add(f);
        return result.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The on-disk file(s) that host the given source path's CAB (no dependency walk).</summary>
    public IReadOnlyList<string> GetCabNamesForSource(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(_baseFolder)) return [];
        string relative = Path.GetRelativePath(_baseFolder, fullPath);
        return _pathToCabs.TryGetValue(relative, out List<string>? cabs) ? cabs : [];
    }

    private string[] Bfs(Queue<string> queue)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string cab = queue.Dequeue();
            if (!visited.Add(cab)) continue;
            if (!_entries.TryGetValue(cab, out Entry? entry)) continue;
            string full = Path.GetFullPath(Path.Combine(_baseFolder, entry.RelativePath));
            if (File.Exists(full)) result.Add(full);
            foreach (string dep in entry.Dependencies) queue.Enqueue(dep);
        }
        return result.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Build a typed map over <paramref name="rootFolder"/> (one file at a time) and write it to
    /// <paramref name="outPath"/>. The caller must already have the right game hook applied so encrypted
    /// bundles load. Returns the number of CABs indexed.
    /// </summary>
    public static int Build(string rootFolder, string outPath)
    {
        string fullRoot = Path.GetFullPath(rootFolder);
        Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(fullRoot, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                GameFileLoader.LoadAndProcess([file]);
                if (!GameFileLoader.IsLoaded) continue;
                string relative = Path.GetRelativePath(fullRoot, file);
                foreach (var collection in GameFileLoader.GameBundle.FetchAssetCollections())
                {
                    string cab = string.IsNullOrWhiteSpace(collection.Name) ? Path.GetFileName(file) : collection.Name;
                    List<string> deps = collection.Dependencies
                        .Where(static d => d is not null && !string.IsNullOrWhiteSpace(d.Name))
                        .Select(static d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    HashSet<int> classIds = new();
                    foreach (IUnityObjectBase asset in collection) classIds.Add(asset.ClassID);
                    entries[cab] = new Entry(relative, deps, classIds.ToList());
                }
            }
            catch { /* not an asset file */ }
            finally { GameFileLoader.Reset(); }
        }

        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);
        string relativeBase = Path.GetRelativePath(outDir, fullRoot);
        using FileStream stream = File.Create(outPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(2);
        writer.Write(relativeBase);
        writer.Write(entries.Count);
        foreach ((string cab, Entry e) in entries.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.Write(cab);
            writer.Write(e.RelativePath);
            writer.Write(0L);
            writer.Write(e.Dependencies.Count);
            foreach (string d in e.Dependencies) writer.Write(d);
            writer.Write(e.ClassIds.Count);
            foreach (int c in e.ClassIds) writer.Write(c);
        }
        return entries.Count;
    }
}
