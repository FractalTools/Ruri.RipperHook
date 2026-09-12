using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace Ruri.RipperHook.CabMapping;

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

        Func<string, bool>? includeBefore = GameBundleHook.ScanIncludeFile;
        GameBundleHook.ScanIncludeFile = GameBundleHook.CabScanIncludeFile;
        List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)>?[] perFile = new List<(string, string, List<string>, List<int>, List<string>)>?[files.Length];
        ConcurrentDictionary<string, int> failures = new(StringComparer.Ordinal);
        try
        {
            ParallelOptions outerLanes = new() { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 8) };
            Parallel.For(0, files.Length, outerLanes, i => perFile[i] = ScanFullMetadata(files[i], failures));
        }
        finally
        {
            GameBundleHook.ScanIncludeFile = includeBefore;
        }

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

        foreach ((string failure, int count) in failures)
        {
            Logger.Warning(LogCategory.Import, $"[CabMap] {count} file(s) could not be scanned: {failure}");
        }
        if (entries.Count == 0 && !failures.IsEmpty)
        {
            throw new InvalidOperationException($"No CAB could be scanned under '{fullRoot}': {failures.Keys.First()}");
        }
        CabTable.FromEntries(fullRoot, entries).Save(fullOut);
        int named = entries.Values.Count(static e => e.ContainerPaths.Count > 0);
        Console.Error.WriteLine($"[CabMap] {files.Length} files scanned, {entries.Count} CABs ({named} with addressable paths) → {fullOut}");
        return 0;
    }

    /// <summary>
    /// One file's rows. A decoder's own scanner failing is recorded in <paramref name="failures"/>
    /// by message, so a build that yields nothing can say why; the generic path's failures are
    /// not, since every file that is no bundle fails it by design.
    /// </summary>
    internal static List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ScanFullMetadata(string file, ConcurrentDictionary<string, int> failures)
    {
        if (GameBundleHook.ScanChunkFull is { } scanChunk)
        {
            try
            {
                return scanChunk(file);
            }
            catch (Exception ex)
            {
                failures.AddOrUpdate($"{ex.GetType().Name}: {ex.Message}", 1, static (_, count) => count + 1);
                Logger.Verbose(LogCategory.Import, $"[CabMap] Scan '{file}': {ex.GetType().Name}: {ex.Message}");
                return new();
            }
        }

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
                    continue;                }

                foreach (SerializedFile sf in serializedFiles)
                {
                    result.AddRange(GameBundleHook.ReadFullMetadataRows(sf, fallbackName));
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

    public static CabTable LoadTable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return CabTable.Load(path);
    }

    public static string[] ResolveClosureCabNames(CabTable table, IEnumerable<string> seedCabNames)
        => ResolveWalkCabNames(table, seedCabNames, reverse: false);

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
                names.Add(seed);            }
        }
        foreach (int id in reverse ? table.ReverseClosureIds(seedIds) : table.ClosureIds(seedIds))
        {
            names.Add(table.CabName(id));
        }
        return names.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();
    }

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
