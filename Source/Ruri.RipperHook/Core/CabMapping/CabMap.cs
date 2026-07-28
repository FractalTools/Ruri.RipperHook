using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// CABMap: CAB name → (chunk file, chunk-entry file name, dependencies, ClassIDs, readable
/// AssetBundle Container addressable paths). One self-contained file — load it and the whole game
/// is browsable by dependency graph (both directions) AND by readable name. Build it ONCE over the
/// whole game folder (parallel across chunk files AND across the bundles inside each), then every
/// resolve goes through <see cref="CabSelection"/> on the columnar <see cref="CabTable"/>; the
/// helpers here cover the remaining shapes:
///   * <see cref="ResolveCabsForFiles"/> — on-disk chunk files → the CABs they host (the seed step
///     of a plain "load exactly these files" request),
///   * <see cref="ResolveCabsForPaths"/> — addressable container paths → their hosting CABs (the
///     seed step of a scene-placement or Blender-side path selection),
///   * <see cref="ResolveClosureCabNames"/> / <see cref="ResolveReverseClosureCabNames"/> — pure
///     in-memory transitive closure over dependencies / dependents, by CAB name.
///
/// Format: RCM5 only -- the columnar layout documented in <see cref="CabTable"/>. A cabmap is a
/// regenerable cache: a format bump means rebuild, never a multi-format compatibility reader.
/// </summary>
public static class CabMap
{
    public sealed record Entry(string RelativePath, string EntryFileName, List<string> Dependencies, List<int> ClassIds, List<string> ContainerPaths);

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

        // Scan mode: tell the VFS extractor to skip resource payloads (video/audio/tables/streaming),
        // decrypting only the AssetBundles that host a CAB. Reset afterwards so normal loading is unaffected.
        //
        // Two parallel axes: the VFS extractor fans out one worker per inner bundle, and the outer
        // lanes here pipeline the many small chunk files behind the giant ones (EndField packs ~62%
        // of all CABs into a single .chk whose inner scan alone saturates the machine; without outer
        // lanes every other chunk would wait for it). Outer width stays low on purpose — both axes
        // share the thread pool, and each in-flight chunk holds decrypt buffers.
        GameBundleHook.ScanIncludeFile = GameBundleHook.CabScanIncludeFile;
        List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)>?[] perFile = new List<(string, string, List<string>, List<int>, List<string>)>?[files.Length];
        try
        {
            ParallelOptions outerLanes = new() { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 8) };
            Parallel.For(0, files.Length, outerLanes, i => perFile[i] = ScanFullMetadata(files[i]));
        }
        finally
        {
            GameBundleHook.ScanIncludeFile = null;
        }

        // Deterministic merge in directory-enumeration order, exactly like the serial scan wrote it:
        // on a duplicate CAB name the later file wins.
        Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++)
        {
            List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)>? rows = perFile[i];
            if (rows is null || rows.Count == 0)
            {
                continue;
            }
            string relativeFilePath = Path.GetRelativePath(fullRoot, files[i]);
            foreach ((string cab, string entryFileName, List<string> deps, List<int> classIds, List<string> paths) in rows)
            {
                entries[cab] = new Entry(relativeFilePath, entryFileName, deps, classIds, paths);
            }
            perFile[i] = null;
        }

        CabTable.FromEntries(fullRoot, entries).Save(fullOut);
        int named = entries.Values.Count(static e => e.ContainerPaths.Count > 0);
        Console.Error.WriteLine($"[CabMap] {files.Length} files scanned, {entries.Count} CABs ({named} with addressable paths) → {fullOut}");
        return 0;
    }

    /// <summary>
    /// Combined single-pass projection of one on-disk file: SerializedFile metadata (CAB name, deps,
    /// ClassIDs) PLUS the readable names (chunk-entry file name, AssetBundle Container paths). Reads the
    /// tiny AssetBundle object per CAB and nothing else — no other asset is materialized, no processor runs.
    ///
    /// EndField (and the other VFS games) wrap their SerializedFiles in encrypted, content-addressed chunk
    /// containers (a <c>.chk</c> indexed by a sibling <c>&lt;dir&gt;.blc</c> manifest); a bare
    /// <see cref="SchemeReader.LoadFile"/> only ever sees an opaque ResourceFile. The active game hook
    /// exposes <see cref="GameBundleHook.ScanChunkFull"/> — a bounded-memory, parallel scan that decrypts
    /// only CAB-hosting bundles and disposes each right after projection. We fall back to driving
    /// <see cref="GameBundleHook.CustomFilePreInitialize"/> (or a direct scheme read) per file otherwise.
    /// </summary>
    internal static List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ScanFullMetadata(string file)
    {
        if (GameBundleHook.ScanChunkFull is { } scanChunk)
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
        // direct scheme read, then project each resulting SerializedFile. Bundles are disposed as they are
        // read so a whole-game scan stays flat.
        List<(string, string, List<string>, List<int>, List<string>)> result = new();
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
                    // Per-asset virtual-row expansion for non-bundled files (one row per named
                    // Mesh/AnimationClip/Texture/... instead of one opaque row per container file)
                    // -- see GameBundleHook.ReadFullMetadataRows. Bundled files come back as the
                    // single container-path row they always were.
                    result.AddRange(GameBundleHook.ReadFullMetadataRows(sf, fallbackName));
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

    /// <summary>Load a cabmap as the columnar <see cref="CabTable"/> — one sequential stream read
    /// straight into the final buffers (see <see cref="CabTable.Load"/>).</summary>
    public static CabTable LoadTable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return CabTable.Load(path);
    }

    /// <summary>Transitive dependency closure by CAB name (seeds included). An unknown seed name is
    /// reported back in the output — classic BFS "visited" semantics — it just expands nothing.</summary>
    public static string[] ResolveClosureCabNames(CabTable table, IEnumerable<string> seedCabNames)
        => ResolveWalkCabNames(table, seedCabNames, reverse: false);

    /// <summary>Transitive DEPENDENT closure by CAB name (seeds included): every CAB that directly
    /// or indirectly references a seed. The mirror of <see cref="ResolveClosureCabNames"/> on the
    /// transposed graph, same unknown-seed semantics.</summary>
    public static string[] ResolveReverseClosureCabNames(CabTable table, IEnumerable<string> seedCabNames)
        => ResolveWalkCabNames(table, seedCabNames, reverse: true);

    private static string[] ResolveWalkCabNames(CabTable table, IEnumerable<string> seedCabNames, bool reverse)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<int> seedIds = new();
        foreach (string seed in seedCabNames)
        {
            if (table.TryGetId(seed, out int id))
            {
                seedIds.Add(id);
            }
            else
            {
                names.Add(seed); // unknown seed: classic Bfs still reported it as visited
            }
        }
        foreach (int id in reverse ? table.ReverseClosureIds(seedIds) : table.ClosureIds(seedIds))
        {
            names.Add(table.CabName(id));
        }
        return names.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// On-disk chunk files -> the CAB names they host. The seed step for a plain
    /// "load exactly these files (plus what they need)" request. Distinct-file resolution runs once
    /// per chunk file the map knows (a few dozen), never per CAB.
    /// </summary>
    public static string[] ResolveCabsForFiles(CabTable table, IEnumerable<string> files)
    {
        HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            if (!string.IsNullOrWhiteSpace(file))
            {
                wanted.Add(Path.GetFullPath(file));
            }
        }
        bool[] matchByFile = new bool[table.FileCount];
        bool any = false;
        for (int fileId = 0; fileId < table.FileCount; fileId++)
        {
            matchByFile[fileId] = wanted.Contains(Path.GetFullPath(Path.Combine(table.BaseFolder, table.DistinctFile(fileId))));
            any |= matchByFile[fileId];
        }
        if (!any)
        {
            return [];
        }
        List<string> cabs = new();
        for (int id = 0; id < table.Count; id++)
        {
            if (matchByFile[table.FileIndex[id]])
            {
                cabs.Add(table.CabName(id));
            }
        }
        return cabs.ToArray();
    }

    /// <summary>
    /// Addressable container paths -> the CAB names hosting them. Case-insensitive, and a path's
    /// <c>##subname</c> suffix (a multi-object FBX sub-asset) is ignored on both sides. One parallel
    /// pass over the path column probing a span lookup of the queries — no 438k-string index is ever
    /// materialized for what is always a handful-to-thousands of queries.
    /// </summary>
    public static string[] ResolveCabsForPaths(CabTable table, IEnumerable<string> containerPaths)
    {
        HashSet<string> queries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in containerPaths)
        {
            int hashIndex = path.IndexOf("##", StringComparison.Ordinal);
            queries.Add(hashIndex >= 0 ? path[..hashIndex] : path);
        }
        if (queries.Count == 0)
        {
            return [];
        }
        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup = queries.GetAlternateLookup<ReadOnlySpan<char>>();

        ConcurrentBag<List<int>> partitions = new();
        Parallel.ForEach(Partitioner.Create(0, table.Count), range =>
        {
            (int start, int end) = range;
            List<int> local = new();
            char[] buffer = ArrayPool<char>.Shared.Rent(Math.Max(1, table.MaxContainerPathUtf8Length));
            try
            {
                for (int id = start; id < end; id++)
                {
                    int pathCount = table.ContainerPathCount(id);
                    for (int i = 0; i < pathCount; i++)
                    {
                        ReadOnlySpan<byte> utf8 = table.ContainerPathUtf8(id, i);
                        int hashIndex = utf8.IndexOf("##"u8);
                        if (hashIndex >= 0)
                        {
                            utf8 = utf8[..hashIndex];
                        }
                        int written = Encoding.UTF8.GetChars(utf8, buffer);
                        if (lookup.Contains(buffer.AsSpan(0, written)))
                        {
                            local.Add(id);
                            break;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
            if (local.Count > 0)
            {
                partitions.Add(local);
            }
        });

        List<string> cabs = new();
        foreach (List<int> local in partitions)
        {
            foreach (int id in local)
            {
                cabs.Add(table.CabName(id));
            }
        }
        cabs.Sort(StringComparer.OrdinalIgnoreCase);
        return cabs.ToArray();
    }
}
