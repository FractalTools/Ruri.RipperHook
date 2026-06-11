using AssetRipper.Assets;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using System.Text;

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
        foreach (string file in files)
        {
            scanned++;
            try
            {
                GameFileLoader.LoadAndProcess([file]);
                if (!GameFileLoader.IsLoaded) continue;

                string relativeFilePath = Path.GetRelativePath(fullRoot, file);
                foreach (var collection in GameFileLoader.GameBundle.FetchAssetCollections())
                {
                    string cabName = string.IsNullOrWhiteSpace(collection.Name)
                        ? Path.GetFileName(file)
                        : collection.Name;
                    List<string> deps = collection.Dependencies
                        .Where(static d => d is not null && !string.IsNullOrWhiteSpace(d.Name))
                        .Select(static d => d.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    HashSet<int> classIds = new();
                    foreach (IUnityObjectBase asset in collection)
                    {
                        classIds.Add((int)asset.ClassID);
                    }
                    entries[cabName] = new Entry(relativeFilePath, 0, deps, classIds.ToList());
                }
            }
            catch (Exception ex)
            {
                Logger.Verbose(LogCategory.Import, $"[CabMap] Skip '{file}': {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                GameFileLoader.Reset();
            }
        }

        Save(fullOut, fullRoot, entries);
        Console.Error.WriteLine($"[CabMap] {scanned} files scanned, {entries.Count} CABs → {fullOut}");
        return 0;
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

    private static void Bfs(string baseFolder, Dictionary<string, Entry> entries, Queue<string> queue, HashSet<string> resultFiles)
    {
        HashSet<string> visitedCabs = new(StringComparer.OrdinalIgnoreCase);
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
