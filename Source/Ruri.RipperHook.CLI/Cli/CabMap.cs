using AssetRipper.Assets.Bundles;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using System.Text;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// Minimal CABMap (CAB name → relative file path + dependencies) so the CLI can resolve a
/// chk's full transitive dependency set without scanning the whole game folder at load time.
/// File format is identical to the GUI's Asset Browser CABMap (BinaryWriter — base-folder
/// string, count, then { cab; relativePath; long offset; depCount; deps[] }), so CLI-built
/// .bin files load in the GUI and vice versa.
///
/// We don't ship the AssetMap (.map MessagePack) sibling here — the CLI only needs the CABMap
/// to chase dependencies for `--load`. The GUI keeps owning the asset-name search side.
/// </summary>
internal static class CabMap
{
    internal sealed record Entry(string RelativePath, long Offset, List<string> Dependencies);

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
                    entries[cabName] = new Entry(relativeFilePath, 0, deps);
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
            entries[cab] = new Entry(relativePath, offset, deps);
        }
        return (baseFolder, entries);
    }

    /// <summary>
    /// Walk the dep graph from each starting file. A "starting file" is given as an on-disk
    /// path; we find the CAB(s) that originate there by matching <see cref="Entry.RelativePath"/>,
    /// then BFS through their dependency CABs. Returns the union of every on-disk file the
    /// transitive closure points to (always including the starting files themselves so AR sees
    /// the seed even when it's not registered as a CAB host).
    /// </summary>
    public static string[] ResolveDeps(string baseFolder, Dictionary<string, Entry> entries, IEnumerable<string> startFiles)
    {
        // Reverse index: relativePath → CAB names that live there.
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

        HashSet<string> resultFiles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> visitedCabs = new(StringComparer.OrdinalIgnoreCase);
        Queue<string> queue = new();

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
                foreach (string cab in cabs) queue.Enqueue(cab);
            }
        }

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

        return resultFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Save(string outPath, string baseFolder, IReadOnlyDictionary<string, Entry> entries)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);
        string relativeBase = Path.GetRelativePath(outDir, baseFolder);

        using FileStream stream = File.Create(outPath);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(relativeBase);
        writer.Write(entries.Count);
        foreach ((string cab, Entry e) in entries.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.Write(cab);
            writer.Write(e.RelativePath);
            writer.Write(e.Offset);
            writer.Write(e.Dependencies.Count);
            foreach (string d in e.Dependencies) writer.Write(d);
        }
    }
}
