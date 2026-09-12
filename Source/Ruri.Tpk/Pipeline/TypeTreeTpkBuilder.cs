using AssetRipper.Primitives;
using AssetRipper.Tpk;
using AssetRipper.Tpk.TypeTrees;
using AssetRipper.Tpk.TypeTrees.Json;
using Ruri.Hook.Core;
using Ruri.RipperHook.Core;
using Ruri.RipperHook.Core.TypeTree;
using VersionClassPair = System.Collections.Generic.KeyValuePair<
    AssetRipper.Primitives.UnityVersion,
    AssetRipper.Tpk.TypeTrees.TpkUnityClass?>;

namespace Ruri.Tpk.Pipeline;

internal static class TypeTreeTpkBuilder
{
    private const string CommonDirectoryName = "Common";

    public static void WriteFromDumpRoot(string dumpRoot, string outputPath)
    {
        List<Snapshot> engine = ReadEngineSnapshots(dumpRoot);
        List<LineageSource> lineages = ReadLineageSources(dumpRoot, engine);

        TpkCollectionBlob collection = new();
        TypeTreeManifest manifest = new();

        foreach (LineageSource lineage in lineages)
        {
            Console.WriteLine($"[Build] lineage {lineage.Key}: {lineage.EngineChain.Count} engine + {lineage.GameChain.Count} game snapshots");

            List<Snapshot> chain = new(lineage.EngineChain.Count + lineage.GameChain.Count);
            chain.AddRange(lineage.EngineChain);
            chain.AddRange(lineage.GameChain);

            TpkTypeTreeBlob blob = BuildLineageBlob(chain, lineage.EngineChain.Count);

            blob = TypeTreeRenamer.ApplyAssetRipperRenaming(blob);

            collection.Add(lineage.Key, blob);
            manifest.Lineages.Add(new TypeTreeManifest.LineageEntry
            {
                Key = lineage.Key,
                Versions = chain.ConvertAll(snapshot => new TypeTreeManifest.VersionEntry
                {
                    Key = snapshot.VersionKey,
                    Engine = snapshot.EngineVersion.ToString(),
                }),
            });
        }

        collection.Add(TypeTreeManifest.BlobName, new TpkJsonBlob { Text = manifest.ToJson() });

        TpkFile.FromBlob(collection, TpkCompressionType.Brotli).WriteToFile(outputPath);
        Console.WriteLine($"[Build] Wrote {Path.GetFileName(outputPath)} from {dumpRoot} to {outputPath}");
    }


    private sealed record Snapshot(string VersionKey, string JsonPath, UnityVersion EngineVersion);

    private sealed record LineageSource(string Key, List<Snapshot> EngineChain, List<Snapshot> GameChain);

    private static List<Snapshot> ReadEngineSnapshots(string dumpRoot)
    {
        string commonDirectory = Path.Combine(dumpRoot, CommonDirectoryName);
        if (!Directory.Exists(commonDirectory))
        {
            throw new DirectoryNotFoundException(
                $"[Build] {commonDirectory} is missing. The stock Unity dumps are the base every game's chain starts from; " +
                "without them a game's lookups would only see the classes it redefines itself.");
        }

        List<Snapshot> snapshots = new();
        foreach (FileInfo file in new DirectoryInfo(commonDirectory).GetFiles("*.json"))
        {
            string key = Path.GetFileNameWithoutExtension(file.Name);
            snapshots.Add(new Snapshot(key, file.FullName, UnityVersion.Parse(key)));
        }

        snapshots.Sort(static (left, right) => left.EngineVersion.CompareTo(right.EngineVersion));
        return snapshots;
    }

    private static List<LineageSource> ReadLineageSources(string dumpRoot, List<Snapshot> engine)
    {
        List<LineageSource> lineages = new();

        foreach (DirectoryInfo directory in new DirectoryInfo(dumpRoot).GetDirectories())
        {
            if (directory.Name is CommonDirectoryName or "output" || directory.Name.StartsWith('.'))
            {
                continue;
            }

            List<Snapshot> gameChain = new();
            foreach (DirectoryInfo versionDirectory in directory.GetDirectories())
            {
                string infoPath = Path.Combine(versionDirectory.FullName, "info.json");
                if (!File.Exists(infoPath))
                {
                    continue;
                }
                gameChain.Add(new Snapshot(versionDirectory.Name, infoPath, ReadEngineVersion(infoPath)));
            }

            if (gameChain.Count == 0)
            {
                continue;
            }

            gameChain.Sort(static (left, right) => VersionKey.Compare(left.VersionKey, right.VersionKey));

            UnityVersion baseEngine = gameChain[^1].EngineVersion;
            List<Snapshot> engineChain = engine.FindAll(snapshot => snapshot.EngineVersion <= baseEngine);
            if (engineChain.Count == 0)
            {
                throw new InvalidOperationException(
                    $"[Build] Lineage '{directory.Name}' reports engine {baseEngine}, but no stock Unity dump is that old or older.");
            }

            lineages.Add(new LineageSource(directory.Name, engineChain, gameChain));
        }

        lineages.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return lineages;
    }

    private static UnityVersion ReadEngineVersion(string infoPath)
    {
        using StreamReader reader = new(infoPath);
        char[] window = new char[4096];
        int read = reader.Read(window, 0, window.Length);
        string head = new(window, 0, read);

        const string marker = "\"Version\"";
        int start = head.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            int open = head.IndexOf('"', head.IndexOf(':', start + marker.Length) + 1);
            int close = head.IndexOf('"', open + 1);
            if (open > 0 && close > open)
            {
                return UnityVersion.Parse(head[(open + 1)..close]);
            }
        }

        throw new InvalidDataException($"[Build] {infoPath} has no readable \"Version\" property.");
    }


    private static TpkTypeTreeBlob BuildLineageBlob(List<Snapshot> chain, int engineCount)
    {
        TpkTypeTreeBlob blob = new();
        blob.CommonString.Add(UnityVersion.MinVersion, []);

        TpkCommonString.Entry[] latestCommonString = [];
        Dictionary<int, string> latestDumped = new();
        Dictionary<int, TpkClassInformation> classDictionary = new();

        for (int ordinal = 0; ordinal < chain.Count; ordinal++)
        {
            Snapshot snapshot = chain[ordinal];
            bool isEngine = ordinal < engineCount;
            UnityVersion key = TypeTreeOrdinal.ToUnityVersion(ordinal);

            UnityInfo info = UnityInfo.ReadFromJsonFile(snapshot.JsonPath);
            Console.WriteLine($"[Build/Tpk]   [{ordinal}] {snapshot.VersionKey}{(isEngine ? " (engine)" : "")}");
            blob.Versions.Add(key);

            TpkCommonString.Entry[] commonString = new TpkCommonString.Entry[info.Strings.Count];
            for (int i = 0; i < info.Strings.Count; i++)
            {
                UnityString unityString = info.Strings[i];
                commonString[i] = new TpkCommonString.Entry(checked((ushort)unityString.Index), unityString.String, blob.StringBuffer);
            }
            if (!commonString.AsSpan().SequenceEqual(latestCommonString))
            {
                latestCommonString = commonString;
                blob.CommonString.Add(key, commonString);
            }

            foreach (UnityClass unityClass in info.Classes)
            {
                if (!isEngine && unityClass.TypeID == 310)
                {
                    continue;
                }

                string dump = unityClass.ToJsonString();
                if (!latestDumped.TryGetValue(unityClass.TypeID, out string? cachedDump) || cachedDump != dump)
                {
                    latestDumped[unityClass.TypeID] = dump;
                    if (!classDictionary.TryGetValue(unityClass.TypeID, out TpkClassInformation? classInformation))
                    {
                        classInformation = new TpkClassInformation(unityClass.TypeID);
                        classDictionary.Add(unityClass.TypeID, classInformation);
                    }

                    classInformation.Classes.Add(new VersionClassPair(key, ClassConversion.Convert(unityClass, blob.StringBuffer, blob.NodeBuffer)));
                }
            }

            if (isEngine)
            {
                HashSet<int> present = new(info.Classes.Select(c => c.TypeID));
                foreach (int missing in classDictionary.Keys.Where(id => !present.Contains(id)).ToList())
                {
                    if (!string.IsNullOrEmpty(latestDumped[missing]))
                    {
                        latestDumped[missing] = string.Empty;
                        classDictionary[missing].Classes.Add(new VersionClassPair(key, null));
                    }
                }
            }
        }

        foreach (TpkClassInformation classInformation in classDictionary.Values)
        {
            VersionClassPair[] pairs = classInformation.Classes.ToArray();
            TpkUnityClass? previous = pairs[0].Value;
            for (int i = 1; i < pairs.Length; i++)
            {
                if (pairs[i].Value == previous)
                {
                    classInformation.Classes.Remove(pairs[i]);
                }
                else
                {
                    previous = pairs[i].Value;
                }
            }
        }

        blob.ClassInformation.AddRange(classDictionary.Values);
        blob.CreationTime = DateTime.UtcNow;
        return blob;
    }
}
