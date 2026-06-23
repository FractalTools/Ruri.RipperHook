using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// CABMap (CAB name → relative file path + dependencies + the ClassIDs it contains) so the CLI can
/// resolve, without loading the whole game into memory, exactly which on-disk files to hand AR for a
/// given target. Build it ONCE over the whole game folder (one file at a time, low peak memory), then:
///   * <see cref="ResolveDeps"/> — transitive dependency closure of some seed files (the old behaviour),
///   * <see cref="ResolveByTypes"/> — every CAB that actually contains an asset of a wanted ClassID,
///     plus their transitive dependencies. This is the "build map then precisely filter" path: e.g.
///     export only shaders by loading just the shader-bearing bundles instead of the entire game.
///
/// Format: a magic+version header, then base-folder, count, then per CAB
/// { cab; relativePath; long offset; depCount; deps[]; classIdCount; classIds[] }.
/// <see cref="Load"/> also still reads the older headerless format (no ClassIDs) the GUI Asset Browser
/// writes — those just resolve to an empty type set.
/// </summary>
internal static class CabMap
{
    private const uint Magic = 0x52434D32; // "RCM2"

    internal sealed record Entry(string RelativePath, long Offset, List<string> Dependencies, List<int> ClassIds);

    public static int Build(string rootFolder, string outPath)
    {
        if (!Directory.Exists(rootFolder))
        {
            Console.Error.WriteLine($"[CabMap] Root folder not found: {rootFolder}");
            return 1;
        }
        string fullRoot = Path.GetFullPath(rootFolder);
        string fullOut = Path.GetFullPath(outPath);
        string[] files = Directory.GetFiles(fullRoot, "*.*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"[CabMap] No files under {fullRoot}");
            return 1;
        }

        Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;

        // Scan mode: tell the VFS extractor to skip resource payloads (video/audio/tables/streaming),
        // decrypting only the AssetBundles that host a CAB. Reset afterwards so normal loading is unaffected.
        // The parallelism that matters lives *inside* the VFS extractor (one worker per inner bundle file):
        // EndField packs ~62% of all CABs into a single .chk, so per-chunk parallelism barely helps — the
        // per-bundle decrypt + decompress + metadata parse is what has to scale across cores.
        GameBundleHook.ScanIncludeFile = GameBundleHook.CabScanIncludeFile;
        try
        {
            foreach (string file in files)
            {
                scanned++;
                string relativeFilePath = Path.GetRelativePath(fullRoot, file);
                foreach ((string cab, List<string> deps, List<int> classIds) in ScanSerializedMetadata(file))
                {
                    entries[cab] = new Entry(relativeFilePath, 0, deps, classIds);
                }
            }
        }
        finally
        {
            GameBundleHook.ScanIncludeFile = null;
        }

        Save(fullOut, fullRoot, entries);
        Console.Error.WriteLine($"[CabMap] {scanned} files scanned, {entries.Count} CABs → {fullOut}");
        return 0;
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
    /// is the slow part of <c>LoadAndProcess</c> we deliberately skip here — the whole point of building
    /// the map fast. We fall back to a direct scheme read when no game hook is active (plain bundles).
    /// </summary>
    internal static List<(string Cab, List<string> Deps, List<int> ClassIds)> ScanSerializedMetadata(string file)
    {
        // Fast path: a VFS game hook (EndField) exposes a bounded-memory, parallel scan that decrypts and
        // reads only the CAB-hosting bundles inside the chunk, disposing each as it goes — see
        // VirtualFileSystem.ScanChunkMetadata. This is the difference between a ~10 min, 21 GB scan and a
        // fast, flat-memory one on a game that packs >250k bundles into a handful of chunks.
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
        // direct scheme read, then project each resulting SerializedFile's metadata. Bundles are disposed
        // as they are read so a whole-game scan stays flat.
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
                // Free the decompressed bundle bytes immediately — a whole-game scan would balloon otherwise.
                (fileBase as IDisposable)?.Dispose();
            }
        }

        return result;
    }

    public static (string baseFolder, Dictionary<string, Entry> entries) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string mapDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);

        bool typed = stream.Length >= 4 && reader.ReadUInt32() == Magic;
        if (typed)
        {
            reader.ReadInt32(); // version, reserved
        }
        else
        {
            stream.Position = 0; // headerless legacy format: rewind and read base string directly
        }

        string storedBase = reader.ReadString();
        string baseFolder = Path.GetFullPath(Path.Combine(mapDir, storedBase));

        int count = reader.ReadInt32();
        Dictionary<string, Entry> entries = new(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string cab = reader.ReadString();
            string relativePath = reader.ReadString();
            long offset = reader.ReadInt64();
            int depCount = reader.ReadInt32();
            List<string> deps = new(depCount);
            for (int j = 0; j < depCount; j++) deps.Add(reader.ReadString());

            List<int> classIds;
            if (typed)
            {
                int classCount = reader.ReadInt32();
                classIds = new List<int>(classCount);
                for (int j = 0; j < classCount; j++) classIds.Add(reader.ReadInt32());
            }
            else
            {
                classIds = [];
            }
            entries[cab] = new Entry(relativePath, offset, deps, classIds);
        }
        return (baseFolder, entries);
    }

    /// <summary>
    /// Transitive dependency closure of the given seed files (the original behaviour). Always includes
    /// the seed files themselves so AR sees the seed even when it isn't registered as a CAB host.
    /// </summary>
    public static string[] ResolveDeps(string baseFolder, Dictionary<string, Entry> entries, IEnumerable<string> startFiles)
    {
        Dictionary<string, List<string>> pathToCabs = BuildPathIndex(entries);

        HashSet<string> resultFiles = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> seeds = new();
        foreach (string raw in startFiles)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string fullPath = Path.GetFullPath(raw);
            resultFiles.Add(fullPath);

            string relative = string.IsNullOrWhiteSpace(baseFolder)
                ? Path.GetFileName(fullPath)
                : Path.GetRelativePath(baseFolder, fullPath);
            if (pathToCabs.TryGetValue(relative, out var cabs))
            {
                foreach (string cab in cabs) seeds.Enqueue(cab);
            }
        }

        Bfs(baseFolder, entries, seeds, resultFiles);
        return resultFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Every on-disk file that hosts a CAB containing an asset of one of <paramref name="targetClassIds"/>,
    /// plus the transitive dependencies of those CABs. The "precise filter" path — load just these.
    /// </summary>
    public static string[] ResolveByTypes(string baseFolder, Dictionary<string, Entry> entries, IReadOnlySet<int> targetClassIds)
    {
        HashSet<string> resultFiles = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> seeds = new();
        int seedCabs = 0;
        foreach ((string cab, Entry e) in entries)
        {
            if (e.ClassIds.Any(targetClassIds.Contains))
            {
                seeds.Enqueue(cab);
                seedCabs++;
            }
        }

        Bfs(baseFolder, entries, seeds, resultFiles);
        Console.Error.WriteLine($"[CabMap] type filter: {seedCabs} CABs host the target type(s) → {resultFiles.Count} file(s) (with dependencies)");
        return resultFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // ── name index (CAB → chunk-entry file name + its AssetBundle Container addressable paths) ─────
    //
    // The CAB map keys everything by content hash; the readable names ("…/pelica/…") live only inside
    // each bundle's AssetBundle Container. A name index is a sidecar — "<cabmap>.names" — built once by a
    // bounded scan that reads ONLY the AssetBundle object per CAB, so the CAB map itself stays untouched
    // (the user's whole point: use the map directly). Each CAB also records the chunk-entry file name that
    // hosts it (e.g. Data/Bundles/Windows/main/<hash>.ab) — which differs from the inner CAB name — because
    // a scoped load must filter chunk entries by THAT name. Pair a name match with the CAB map's dependency
    // graph and you get "every asset called pelica, plus its full dependency closure".

    internal sealed record NameEntry(string FileName, List<string> Paths);

    private const uint NameMagic = 0x524E4D32; // "RNM2" (v2 adds the per-CAB chunk-entry file name)

    /// <summary>The sidecar name-index path for a CAB map: <c>&lt;cabmap&gt;.names</c>.</summary>
    public static string NameIndexPath(string cabMapPath) => Path.ChangeExtension(Path.GetFullPath(cabMapPath), ".names");

    /// <summary>True if <paramref name="path"/> is a current-format name index; false (rebuild) if missing or stale.</summary>
    public static bool IsNameIndexCurrent(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
            return stream.Length >= 4 && reader.ReadUInt32() == NameMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Build a name index over <paramref name="rootFolder"/> (one file at a time, bounded memory) and write
    /// it to <paramref name="outPath"/>. The active game hook must be applied so encrypted bundles decrypt.
    /// Returns the number of CABs with at least one readable container path.
    /// </summary>
    public static int BuildNameIndex(string rootFolder, string outPath)
    {
        if (!Directory.Exists(rootFolder))
        {
            Console.Error.WriteLine($"[NameIndex] Root folder not found: {rootFolder}");
            return 0;
        }
        string fullRoot = Path.GetFullPath(rootFolder);
        string[] files = Directory.GetFiles(fullRoot, "*.*", SearchOption.AllDirectories);

        // Every CAB is recorded (even when its container has no paths) so its chunk-entry file name is
        // always available for the scoped load filter; the paths drive name search and may be empty.
        Dictionary<string, NameEntry> index = new(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;

        GameBundleHook.ScanIncludeFile = GameBundleHook.CabScanIncludeFile;
        try
        {
            foreach (string file in files)
            {
                scanned++;
                int before = index.Count;
                foreach ((string cab, string fileName, List<string> paths) in ScanContainerNames(file))
                {
                    index[cab] = new NameEntry(fileName, paths);
                }
                int added = index.Count - before;
                if (added > 0)
                {
                    Console.Error.WriteLine($"[NameIndex] {Path.GetFileName(file)}: +{added} CABs ({index.Count} total)");
                }
            }
        }
        finally
        {
            GameBundleHook.ScanIncludeFile = null;
        }

        SaveNameIndex(Path.GetFullPath(outPath), index);
        Console.Error.WriteLine($"[NameIndex] {scanned} files scanned, {index.Count} CABs indexed → {Path.GetFullPath(outPath)}");
        return index.Count;
    }

    private static List<(string Cab, string FileName, List<string> Paths)> ScanContainerNames(string file)
    {
        // Fast path: a VFS game hook (EndField) exposes a bounded, parallel container-name scan.
        if (GameBundleHook.ScanChunkNames is { } scanChunk)
        {
            try
            {
                return scanChunk(file);
            }
            catch (Exception ex)
            {
                Logger.Verbose(LogCategory.Import, $"[NameIndex] Scan '{file}': {ex.GetType().Name}: {ex.Message}");
                return new();
            }
        }

        // Fallback (non-VFS games): drive the hook's file-pre-initialize unpack if present, then read each
        // SerializedFile's AssetBundle Container, disposing as we go.
        List<(string, string, List<string>)> result = new();
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
            Logger.Verbose(LogCategory.Import, $"[NameIndex] Unpack '{file}': {ex.GetType().Name}: {ex.Message}");
            return result;
        }

        string fallbackName = Path.GetFileName(file);
        foreach (FileBase fileBase in fileStack)
        {
            try
            {
                if (fileBase is SerializedFile single)
                {
                    result.Add(GameBundleHook.ReadContainerNames(single, fallbackName));
                }
                else if (fileBase is FileContainer container)
                {
                    container.ReadContentsRecursively();
                    foreach (SerializedFile sf in container.FetchSerializedFiles())
                    {
                        result.Add(GameBundleHook.ReadContainerNames(sf, fallbackName));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Verbose(LogCategory.Import, $"[NameIndex] Read '{file}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                (fileBase as IDisposable)?.Dispose();
            }
        }
        return result;
    }

    /// <summary>Load a name index sidecar: CAB → (chunk-entry file name, container paths).</summary>
    public static Dictionary<string, NameEntry> LoadNameIndex(string path)
    {
        Dictionary<string, NameEntry> index = new(StringComparer.OrdinalIgnoreCase);
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
            index[cab] = new NameEntry(fileName, paths);
        }
        return index;
    }

    private static void SaveNameIndex(string outPath, IReadOnlyDictionary<string, NameEntry> index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using FileStream stream = File.Create(outPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(NameMagic);
        writer.Write(index.Count);
        foreach ((string cab, NameEntry entry) in index.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.Write(cab);
            writer.Write(entry.FileName);
            writer.Write(entry.Paths.Count);
            foreach (string p in entry.Paths) writer.Write(p);
        }
    }

    /// <summary>
    /// Resolve a name match to a scoped load: find every CAB whose AssetBundle Container has a path matching
    /// any of <paramref name="nameRegexes"/> (the seeds), take their transitive dependency closure via the
    /// CAB map, and return the on-disk chunk files that host them. <paramref name="loadFilterFileNames"/>
    /// returns the chunk-ENTRY file names (e.g. Data/Bundles/Windows/main/&lt;hash&gt;.ab) of every CAB in
    /// the closure — the exact set a scoped load must extract, so the huge chunks contribute only the few
    /// hundred bundles the target actually needs. <paramref name="matchedCabs"/> is the seed count.
    /// </summary>
    public static string[] ResolveByNames(string baseFolder, Dictionary<string, Entry> entries, Dictionary<string, NameEntry> nameIndex, Regex[] nameRegexes, out int matchedCabs, out HashSet<string> loadFilterFileNames)
    {
        HashSet<string> resultFiles = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> seeds = new();
        int seedCount = 0;
        foreach ((string cab, NameEntry entry) in nameIndex)
        {
            bool matched = false;
            foreach (string p in entry.Paths)
            {
                if (nameRegexes.Any(r => r.IsMatch(p)))
                {
                    matched = true;
                    break;
                }
            }
            if (matched)
            {
                seeds.Enqueue(cab);
                seedCount++;
            }
        }
        matchedCabs = seedCount;

        // Full closure CAB set (seeds + transitive deps), then map each to its chunk-entry file name —
        // the bundle-granular load filter so only these bundles are extracted from the (huge) chunks.
        HashSet<string> closureCabs = new(StringComparer.OrdinalIgnoreCase);
        Bfs(baseFolder, entries, seeds, resultFiles, closureCabs);

        loadFilterFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cab in closureCabs)
        {
            if (nameIndex.TryGetValue(cab, out NameEntry? entry) && !string.IsNullOrEmpty(entry.FileName))
            {
                loadFilterFileNames.Add(entry.FileName);
            }
        }
        return resultFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, List<string>> BuildPathIndex(Dictionary<string, Entry> entries)
    {
        Dictionary<string, List<string>> pathToCabs = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string cab, Entry e) in entries)
        {
            if (!pathToCabs.TryGetValue(e.RelativePath, out var list))
            {
                list = [];
                pathToCabs[e.RelativePath] = list;
            }
            list.Add(cab);
        }
        return pathToCabs;
    }

    private static void Bfs(string baseFolder, Dictionary<string, Entry> entries, Queue<string> queue, HashSet<string> resultFiles, HashSet<string>? visitedOut = null)
    {
        HashSet<string> visitedCabs = visitedOut ?? new(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string cab = queue.Dequeue();
            if (!visitedCabs.Add(cab)) continue;
            if (!entries.TryGetValue(cab, out Entry? entry)) continue;

            if (!string.IsNullOrWhiteSpace(baseFolder))
            {
                string full = Path.GetFullPath(Path.Combine(baseFolder, entry.RelativePath));
                if (File.Exists(full)) resultFiles.Add(full);
            }

            foreach (string dep in entry.Dependencies) queue.Enqueue(dep);
        }
    }

    private static void Save(string outPath, string baseFolder, IReadOnlyDictionary<string, Entry> entries)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);
        string relativeBase = Path.GetRelativePath(outDir, baseFolder);

        using FileStream stream = File.Create(outPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(2); // version
        writer.Write(relativeBase);
        writer.Write(entries.Count);
        foreach ((string cab, Entry e) in entries.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.Write(cab);
            writer.Write(e.RelativePath);
            writer.Write(e.Offset);
            writer.Write(e.Dependencies.Count);
            foreach (string d in e.Dependencies) writer.Write(d);
            writer.Write(e.ClassIds.Count);
            foreach (int c in e.ClassIds) writer.Write(c);
        }
    }
}
