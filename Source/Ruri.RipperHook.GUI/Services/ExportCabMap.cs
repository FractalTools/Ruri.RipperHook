using System.Text;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using Ruri.RipperHook.HookUtils.GameBundleHook;

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
    private const uint NameMagic = 0x524E4D32; // "RNM2" — name-index sidecar, keep in sync with CLI CabMap

    private sealed record Entry(string RelativePath, List<string> Dependencies, List<int> ClassIds);

    /// <summary>One CAB's name-index row: the chunk-entry file name that hosts it + its Container paths.</summary>
    internal sealed record NameEntry(string FileName, List<string> Paths);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pathToCabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NameEntry> _nameIndex = new(StringComparer.OrdinalIgnoreCase);
    private string _baseFolder = string.Empty;

    public bool HasMap => _entries.Count > 0;
    public bool HasNames => _nameIndex.Count > 0;
    public int CabCount => _entries.Count;
    public string MapPath { get; private set; } = string.Empty;

    /// <summary>The sidecar name-index path for a CAB map: <c>&lt;cabmap&gt;.names</c> (matches the CLI).</summary>
    public static string NameIndexPath(string cabMapPath) => Path.ChangeExtension(Path.GetFullPath(cabMapPath), ".names");

    /// <summary>One virtual-file row for the browser: a CAB, where it lives, what it holds, how connected it is.</summary>
    internal sealed record CabRow(string Cab, string RelativePath, IReadOnlyList<int> ClassIds, int DependencyCount, IReadOnlyList<string> ContainerPaths);

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
        _nameIndex.Clear();
        _baseFolder = string.Empty;
        MapPath = string.Empty;
    }

    /// <summary>
    /// Load the optional name-index sidecar (CAB → chunk-entry file name + Container paths) written by the
    /// CLI's <c>--build-name-index</c>. Without it the browser still lists CABs by hash; with it every CAB
    /// shows its readable addressable paths and the list becomes searchable by name (e.g. "pelica").
    /// </summary>
    public void LoadNames(string path)
    {
        _nameIndex.Clear();
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != NameMagic)
        {
            throw new InvalidDataException($"Not a name index (or stale format — rebuild it): {path}");
        }
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string cab = reader.ReadString();
            string fileName = reader.ReadString();
            int pathCount = reader.ReadInt32();
            List<string> paths = new(pathCount);
            for (int j = 0; j < pathCount; j++) paths.Add(reader.ReadString());
            _nameIndex[cab] = new NameEntry(fileName, paths);
        }
    }

    /// <summary>Every CAB as a virtual-file row (with its Container paths when a name index is loaded).</summary>
    public IEnumerable<CabRow> EnumerateCabRows()
    {
        foreach ((string cab, Entry entry) in _entries)
        {
            IReadOnlyList<string> paths = _nameIndex.TryGetValue(cab, out NameEntry? name) ? name.Paths : [];
            yield return new CabRow(cab, entry.RelativePath, entry.ClassIds, entry.Dependencies.Count, paths);
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
        HashSet<string> resultFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> closureCabs = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> queue = new();
        foreach (string cab in seedCabs)
        {
            if (!string.IsNullOrWhiteSpace(cab)) queue.Enqueue(cab);
        }

        while (queue.Count > 0)
        {
            string cab = queue.Dequeue();
            if (!closureCabs.Add(cab)) continue;
            if (!_entries.TryGetValue(cab, out Entry? entry)) continue;
            string full = Path.GetFullPath(Path.Combine(_baseFolder, entry.RelativePath));
            if (File.Exists(full)) resultFiles.Add(full);
            foreach (string dep in entry.Dependencies) queue.Enqueue(dep);
        }

        HashSet<string> fileNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string cab in closureCabs)
        {
            if (_nameIndex.TryGetValue(cab, out NameEntry? name) && !string.IsNullOrEmpty(name.FileName))
            {
                fileNames.Add(name.FileName);
            }
        }

        return (resultFiles.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray(), fileNames);
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

        // Scan mode: skip resource payloads in the VFS extractor, decrypting only CAB-hosting bundles. The
        // parallelism that matters lives inside the VFS extractor (one worker per inner bundle file), since
        // EndField packs the bulk of all CABs into a single .chk.
        GameBundleHook.ScanIncludeFile = GameBundleHook.CabScanIncludeFile;
        try
        {
            foreach (string file in Directory.GetFiles(fullRoot, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(fullRoot, file);
                foreach ((string cab, List<string> deps, List<int> classIds) in ScanSerializedMetadata(file))
                {
                    entries[cab] = new Entry(relative, deps, classIds);
                }
            }
        }
        finally
        {
            GameBundleHook.ScanIncludeFile = null;
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

    /// <summary>
    /// Read only the SerializedFile METADATA of one on-disk file — CAB name, dependency identifiers and the
    /// ClassIDs from its type table — WITHOUT materializing a single asset or running any processor.
    ///
    /// EndField (and the other VFS games) wrap their SerializedFiles in encrypted, content-addressed chunk
    /// containers (a <c>.chk</c> indexed by a sibling <c>&lt;dir&gt;.blc</c> manifest); a bare
    /// <see cref="SchemeReader.LoadFile"/> only ever sees an opaque ResourceFile. The active game hook
    /// installs <see cref="GameBundleHook.CustomFilePreInitialize"/> — the exact VFS-unpack step that
    /// <c>GameBundle.InitializeFromPaths</c> runs: it turns a path into a stack of parsed
    /// <see cref="FileBase"/> (decrypt + decompress + read SerializedFile metadata) but does NOT build a
    /// single AssetCollection or read any object's data. That per-asset data read plus the processor pass
    /// is the slow part of <c>LoadAndProcess</c> we deliberately skip. Keep in sync with the CLI CabMap.
    /// </summary>
    private static List<(string Cab, List<string> Deps, List<int> ClassIds)> ScanSerializedMetadata(string file)
    {
        // Fast path: a VFS game hook (EndField) exposes a bounded-memory, parallel scan of the chunk's
        // CAB-hosting bundles (VirtualFileSystem.ScanChunkMetadata), disposing each bundle as it goes.
        if (GameBundleHook.ScanChunk is { } scanChunk)
        {
            try
            {
                return scanChunk(file);
            }
            catch (Exception ex)
            {
                Logger.Verbose(LogCategory.Import, $"[CabMap] Scan '{file}': {ex.GetType().Name}: {ex.Message}");
                return new();
            }
        }

        // Fallback (non-VFS games): drive the hook's file-pre-initialize unpack if present, otherwise a
        // direct scheme read, then project each SerializedFile's metadata; bundles are disposed as read.
        List<(string, List<string>, List<int>)> result = new();
        List<FileBase> fileStack = new();

        try
        {
            GameBundleHook.FilePreInitializeDelegate? preInitialize = GameBundleHook.CustomFilePreInitialize;
            if (preInitialize is not null)
            {
                preInitialize(new GameBundle(), new[] { file }, fileStack, LocalFileSystem.Instance, null);
            }
            else
            {
                fileStack.Add(SchemeReader.LoadFile(file, LocalFileSystem.Instance));
            }
        }
        catch (Exception ex)
        {
            Logger.Verbose(LogCategory.Import, $"[CabMap] Unpack '{file}': {ex.GetType().Name}: {ex.Message}");
            return result;
        }

        string fallbackName = Path.GetFileName(file);
        foreach (FileBase fileBase in fileStack)
        {
            try
            {
                IEnumerable<SerializedFile> serializedFiles;
                if (fileBase is SerializedFile single)
                {
                    serializedFiles = [single];
                }
                else if (fileBase is FileContainer container)
                {
                    container.ReadContentsRecursively();
                    serializedFiles = container.FetchSerializedFiles();
                }
                else
                {
                    continue; // ResourceFile / FailedFile — no asset type table to read
                }

                foreach (SerializedFile sf in serializedFiles)
                {
                    result.Add(GameBundleHook.ReadSerializedMetadata(sf, fallbackName));
                }
            }
            catch (Exception ex)
            {
                Logger.Verbose(LogCategory.Import, $"[CabMap] Read '{file}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                (fileBase as IDisposable)?.Dispose();
            }
        }

        return result;
    }
}
