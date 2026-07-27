using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.CabMapping;

/// <summary>
/// CABMap: CAB name → (relative file path, chunk-entry file name, dependencies, ClassIDs, readable
/// AssetBundle Container addressable paths). One self-contained file — load it and the whole game is
/// browsable by dependency graph AND by readable name, no sidecar needed. Build it ONCE over the whole
/// game folder (single combined scan, bounded memory), then:
///   * <see cref="ResolveDeps"/> — transitive dependency closure of some seed files,
///   * <see cref="ResolveByTypes"/> — every CAB that contains an asset of a wanted ClassID (+ deps),
///   * <see cref="ResolveByNames"/> — every CAB whose addressable path matches a regex (+ deps),
///     including the chunk-entry names a scoped bundle-granular load must filter by,
///   * <see cref="ResolveScopedClosure"/> — same as ResolveByNames' closure step, but seeded directly
///     by known CAB names (no regex) — what a Blender-side browser selection resolves through.
///
/// Format: RCM4 only -- the columnar layout documented in <see cref="CabTable"/> (UTF-8 blobs +
/// offset tables + int-indexed dependency graph), loading as one ReadAllBytes plus buffer
/// slices. A cabmap is a regenerable cache: a format bump means rebuild, never a
/// multi-format compatibility reader.
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
                foreach ((string cab, string entryFileName, List<string> deps, List<int> classIds, List<string> paths) in ScanFullMetadata(file))
                {
                    entries[cab] = new Entry(relativeFilePath, entryFileName, deps, classIds, paths);
                }
            }
        }
        finally
        {
            GameBundleHook.ScanIncludeFile = null;
        }

        Save(fullOut, fullRoot, entries);
        int named = entries.Values.Count(static e => e.ContainerPaths.Count > 0);
        Console.Error.WriteLine($"[CabMap] {scanned} files scanned, {entries.Count} CABs ({named} with addressable paths) → {fullOut}");
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

    /// <summary>
    /// Load a cabmap as the columnar <see cref="CabTable"/>: one ReadAllBytes plus buffer
    /// slices, no per-string parse at all. RCM4 is the ONLY format -- a cabmap is a
    /// regenerable cache, so a format bump means rebuild, never a compatibility reader.
    /// </summary>
    public static CabTable LoadTable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || BitConverter.ToUInt32(file, 0) != CabTable.Magic4)
        {
            throw new InvalidDataException(
                $"'{path}' is not an RCM4 cabmap -- rebuild it (Build writes RCM4 only).");
        }
        return CabTable.LoadRcm4(path, file);
    }






    // ── name index (CAB → chunk-entry file name + its AssetBundle Container addressable paths) ─────
    //
    // RCM3 maps carry these inline (NameIndexFromEntries). The RNM2 sidecar machinery below remains for
    // legacy RCM2 maps: built once by a bounded scan that reads ONLY the AssetBundle object per CAB. Each
    // CAB also records the chunk-entry file name that hosts it (e.g. Data/Bundles/Windows/main/<hash>.ab)
    // — which differs from the inner CAB name — because a scoped load must filter chunk entries by THAT
    // name. Pair a name match with the CAB map's dependency graph and you get "every asset called pelica,
    // plus its full dependency closure".


    private const uint NameMagic = 0x524E4D32; // "RNM2" (v2 adds the per-CAB chunk-entry file name)











    private static string NormalizeContainerPath(string path)
    {
        int hashIdx = path.IndexOf("##", StringComparison.Ordinal);
        return (hashIdx >= 0 ? path[..hashIdx] : path).ToLowerInvariant();
    }




    private static void Save(string outPath, string baseFolder, IReadOnlyDictionary<string, Entry> entries)
    {
        // RCM4 (columnar) is the only written format now -- it loads via buffer slices instead
        // of a per-string walk, stores dependencies as int ids instead of repeated name strings
        // (the bulk of RCM3's size), and is what the pythonnet bridge hands across unchanged.
        // Load() still reads every older format.
        CabTable.FromEntries(baseFolder, new Dictionary<string, Entry>(entries, StringComparer.OrdinalIgnoreCase))
            .Save(outPath);
    }

    // ── columnar (CabTable) resolver overloads ───────────────────────────────
    //
    // Same contracts as the Dictionary-of-Entry overloads above, executed on the int graph:
    // closure output includes unknown seeds and phantom dependency names exactly like the
    // classic BFS did (visited.Add happened before the entry lookup), results sorted the same.

    public static string[] ResolveClosureCabNames(CabTable table, IEnumerable<string> seedCabNames)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<int> seedIds = new();
        foreach (string seed in seedCabNames)
        {
            if (table.CabToId.TryGetValue(seed, out int id))
            {
                seedIds.Add(id);
            }
            else
            {
                names.Add(seed); // unknown seed: classic Bfs still reported it as visited
            }
        }
        foreach (int id in table.ClosureIds(seedIds))
        {
            names.Add(table.CabName(id));
        }
        return names.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();
    }


    /// <summary>
    /// On-disk chunk files -> the CAB names they host. The seed step for a plain
    /// "load exactly these files (plus what they need)" request.
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
        List<string> cabs = new();
        for (int id = 0; id < table.Count; id++)
        {
            string full = Path.GetFullPath(Path.Combine(table.BaseFolder, table.RelativePath(id)));
            if (wanted.Contains(full))
            {
                cabs.Add(table.CabName(id));
            }
        }
        return cabs.ToArray();
    }

    public static string[] ResolveCabsForPaths(CabTable table, IEnumerable<string> containerPaths)
    {
        Dictionary<string, List<int>> index = new(StringComparer.OrdinalIgnoreCase);
        for (int id = 0; id < table.Count; id++)
        {
            int pathCount = table.ContainerPathCount(id);
            for (int p = 0; p < pathCount; p++)
            {
                string key = NormalizeContainerPath(table.ContainerPath(id, p));
                if (!index.TryGetValue(key, out List<int>? ids))
                {
                    index[key] = ids = new List<int>();
                }
                ids.Add(id);
            }
        }
        HashSet<string> cabs = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in containerPaths)
        {
            if (index.TryGetValue(NormalizeContainerPath(path), out List<int>? matches))
            {
                foreach (int id in matches)
                {
                    cabs.Add(table.CabName(id));
                }
            }
        }
        return cabs.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
