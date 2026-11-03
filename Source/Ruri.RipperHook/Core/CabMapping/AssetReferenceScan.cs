using System.Collections.Concurrent;
using System.Diagnostics;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CabMapping;

internal static class AssetReferenceScan
{
    public static Dictionary<string, List<string>> Run(string rootFolder, IReadOnlyDictionary<string, CabMap.Entry> entries)
    {
        Dictionary<string, HashSet<string>> cabsByFile = FilesWithAssetRows(entries);
        if (cabsByFile.Count == 0)
        {
            return [];
        }

        Stopwatch watch = Stopwatch.StartNew();
        ReferenceReport report = new();
        ConcurrentDictionary<string, FileReferences> collected = new(StringComparer.OrdinalIgnoreCase);
        using (ReferenceEnvironment environment = ReferenceEnvironment.Create(rootFolder))
        {
            ScriptHostCache hosts = new(rootFolder, entries, report);
            FileReferenceReader reader = new(environment, hosts, report);
            ParallelOptions lanes = new() { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 4) };
            Parallel.ForEach(cabsByFile, lanes, pair =>
            {
                string relativePath = pair.Key;
                HashSet<string> cabs = pair.Value;
                SerializedFileOpener.Visit(Path.Combine(rootFolder, relativePath),
                    (archiveName, file) =>
                    {
                        string cab = string.IsNullOrWhiteSpace(file.NameFixed) ? archiveName : file.NameFixed;
                        if (cabs.Contains(cab))
                        {
                            collected[cab] = reader.Read(cab, file);
                            Interlocked.Increment(ref report.FilesRead);
                        }
                    },
                    (stage, exception) => report.Failure($"{stage} '{relativePath}'", exception));
            });
        }

        long rawEdges = collected.Values.Sum(static file => (long)file.Edges.Count);
        Dictionary<string, List<string>> references = Resolve(entries, collected, report);
        report.Log(cabsByFile.Values.Sum(static cabs => cabs.Count), rawEdges, references.Values.Sum(static targets => (long)targets.Count), watch);
        return references;
    }

    private static Dictionary<string, HashSet<string>> FilesWithAssetRows(IReadOnlyDictionary<string, CabMap.Entry> entries)
    {
        Dictionary<string, HashSet<string>> cabsByFile = new(StringComparer.OrdinalIgnoreCase);
        if (GameBundleHook.ScanChunkFull is not null)
        {
            return cabsByFile;
        }
        foreach ((string cab, CabMap.Entry entry) in entries)
        {
            if (!GameBundleHook.TryGetAssetRowFile(cab, out string fileCab) || !entries.ContainsKey(fileCab))
            {
                continue;
            }
            if (!cabsByFile.TryGetValue(entry.RelativePath, out HashSet<string>? cabs))
            {
                cabsByFile[entry.RelativePath] = cabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            cabs.Add(fileCab);
        }
        return cabsByFile;
    }

    private static Dictionary<string, List<string>> Resolve(IReadOnlyDictionary<string, CabMap.Entry> entries,
        ConcurrentDictionary<string, FileReferences> collected, ReferenceReport report)
    {
        Dictionary<string, HashSet<string>> targetsBySource = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileReferences file in collected.Values.OrderBy(static file => file.Cab, StringComparer.Ordinal))
        {
            foreach ((long source, int fileId, long target) in file.Edges)
            {
                if (!TryTargetNode(file, fileId, target, entries, collected, report, out string targetNode))
                {
                    continue;
                }
                string sourceNode = source == 0 ? file.Cab : GameBundleHook.AssetRowName(file.Cab, source);
                if (string.Equals(sourceNode, targetNode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!targetsBySource.TryGetValue(sourceNode, out HashSet<string>? targets))
                {
                    targetsBySource[sourceNode] = targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                targets.Add(targetNode);
            }
        }
        return targetsBySource.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryTargetNode(FileReferences file, int fileId, long pathId, IReadOnlyDictionary<string, CabMap.Entry> entries,
        ConcurrentDictionary<string, FileReferences> collected, ReferenceReport report, out string node)
    {
        node = string.Empty;
        FileReferences? holder = file;
        if (fileId != 0)
        {
            string targetFile = fileId > 0 && fileId <= file.DependencyNames.Length ? file.DependencyNames[fileId - 1] : string.Empty;
            if (targetFile.Length == 0)
            {
                Interlocked.Increment(ref report.ReferencesToUnnamedFiles);
                return false;
            }
            if (!entries.ContainsKey(targetFile))
            {
                Interlocked.Increment(ref report.ReferencesOutsideMap);
                node = targetFile;
                return true;
            }
            if (!collected.TryGetValue(targetFile, out holder))
            {
                node = targetFile;
                return true;
            }
        }
        if (!holder.Holds(pathId))
        {
            Interlocked.Increment(ref report.ReferencesToMissingObjects);
            return false;
        }
        long owner = holder.OwnerOf(pathId);
        node = owner == 0 ? holder.Cab : GameBundleHook.AssetRowName(holder.Cab, owner);
        return true;
    }
}
