using AssetRipper.Primitives;
using AssetRipper.Tpk;
using AssetRipper.Tpk.TypeTrees;
using AssetRipper.Tpk.TypeTrees.Json;
using System.Linq;
using VersionClassPair = System.Collections.Generic.KeyValuePair<
    AssetRipper.Primitives.UnityVersion,
    AssetRipper.Tpk.TypeTrees.TpkUnityClass?>;

namespace Ruri.AssemblyDumper.Pipeline;

internal static class TypeTreeTpkBuilder
{
    public static void WriteFromJsonDirectory(string jsonDirectory, string outputPath)
    {
        TpkTypeTreeBlob blob = CreateFromDirectory(jsonDirectory);
        TpkFile.FromBlob(blob, TpkCompressionType.Brotli).WriteToFile(outputPath);
        Console.WriteLine($"[Build] Wrote type_tree.tpk from {jsonDirectory} to {outputPath}");
    }

    private static TpkTypeTreeBlob CreateFromDirectory(string directoryPath)
    {
        IEnumerable<string> orderedPaths = GetOrderedJsonFilePaths(directoryPath);
        return Create(orderedPaths.Select(UnityInfo.ReadFromJsonFile));
    }

    private static IEnumerable<string> GetOrderedJsonFilePaths(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        Dictionary<UnityVersion, string> files = new();
        foreach (FileInfo file in directory.GetFiles("*.json", SearchOption.TopDirectoryOnly))
        {
            UnityVersion version = UnityVersion.Parse(Path.GetFileNameWithoutExtension(file.Name));
            files.Add(version, file.FullName);
        }

        List<UnityVersion> orderedVersions = files.Keys.ToList();
        orderedVersions.Sort();
        return orderedVersions.Select(version => files[version]);
    }

    private static TpkTypeTreeBlob Create(IEnumerable<UnityInfo> infosOrderedByUnityVersion)
    {
        TpkTypeTreeBlob blob = new();
        blob.CommonString.Add(UnityVersion.MinVersion, 0);

        byte latestCommonStringCount = 0;
        List<string> commonStrings = new();
        Dictionary<int, string> latestUnityClassesDumped = new();
        Dictionary<int, TpkClassInformation> classDictionary = new();

        foreach (UnityInfo info in infosOrderedByUnityVersion)
        {
            Console.WriteLine($"[Build/Tpk] {info.Version}");
            UnityVersion version = UnityVersion.Parse(info.Version);
            blob.Versions.Add(version);

            if (info.Strings.Count != latestCommonStringCount)
            {
                latestCommonStringCount = checked((byte)info.Strings.Count);
                blob.CommonString.Add(version, latestCommonStringCount);
            }

            for (int i = 0; i < info.Strings.Count; i++)
            {
                if (i < commonStrings.Count)
                {
                    if (info.Strings[i].String != commonStrings[i])
                    {
                        throw new Exception($"String inequality at index {i} for version {version}");
                    }
                }
                else
                {
                    commonStrings.Add(info.Strings[i].String);
                }
            }

            foreach (UnityClass unityClass in info.Classes)
            {
                // Custom overlays sometimes ship a STRIPPED UnityConnectSettings(310) (StarRail 2.1.0+:
                // 6 fields, no CrashReportingSettings/UnityPurchasingSettings landmark) that breaks
                // Pass506's IL-insertion. 310 is connect/analytics settings, never in game asset
                // bundles, so drop the overlay's 310 and let the real full definition carry forward.
                if (version.Type == UnityVersionType.Experimental && unityClass.TypeID == 310)
                    continue;

                string dump = unityClass.ToJsonString();
                if (!latestUnityClassesDumped.TryGetValue(unityClass.TypeID, out string? cachedDump) || cachedDump != dump)
                {
                    latestUnityClassesDumped[unityClass.TypeID] = dump;
                    if (!classDictionary.TryGetValue(unityClass.TypeID, out TpkClassInformation? tpkClassInformation))
                    {
                        tpkClassInformation = new TpkClassInformation(unityClass.TypeID);
                        classDictionary.Add(unityClass.TypeID, tpkClassInformation);
                    }

                    TpkUnityClass tpkUnityClass = ClassConversion.Convert(unityClass, blob.StringBuffer, blob.NodeBuffer);
                    tpkClassInformation.Classes.Add(new VersionClassPair(version, tpkUnityClass));
                }
            }

            // Custom-engine dumps (UnityVersionType.Experimental, TypeNumber = CustomEngineType) are
            // partial OVERLAYS on their base Unity version, not full snapshots: a class they omit must
            // keep the base-Unity definition, not be null-marked. Null-marking here would drop every
            // ancestor an overlay doesn't re-dump (EndField is ECS — leaf components but no
            // GameObject/Component/Behaviour/Renderer/... chain), nulling them across the overlay's
            // version range and breaking base-class resolution (Pass005) + version-instance lookups.
            // Skipping it lets omitted classes carry forward, which is the correct overlay model.
            if (version.Type != UnityVersionType.Experimental)
            {
                List<int> typeIds = info.Classes.Select(c => c.TypeID).ToList();
                foreach (int unusedId in classDictionary.Keys.Where(id => !typeIds.Contains(id)).ToList())
                {
                    if (!string.IsNullOrEmpty(latestUnityClassesDumped[unusedId]))
                    {
                        latestUnityClassesDumped[unusedId] = string.Empty;
                        classDictionary[unusedId].Classes.Add(new VersionClassPair(version, null));
                    }
                }
            }
        }

        foreach (TpkClassInformation tpkClassInfo in classDictionary.Values)
        {
            VersionClassPair[] pairs = tpkClassInfo.Classes.ToArray();
            TpkUnityClass? previousClass = pairs[0].Value;
            for (int i = 1; i < pairs.Length; i++)
            {
                VersionClassPair pair = pairs[i];
                if (pair.Value == previousClass)
                {
                    tpkClassInfo.Classes.Remove(pair);
                }
                else
                {
                    previousClass = pair.Value;
                }
            }
        }

        blob.ClassInformation.AddRange(classDictionary.Values);
        blob.CommonString.SetIndices(blob.StringBuffer, commonStrings);
        blob.CreationTime = DateTime.UtcNow;
        return blob;
    }
}
