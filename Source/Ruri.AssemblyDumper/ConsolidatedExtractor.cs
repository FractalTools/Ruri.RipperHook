using AssetRipper.DocExtraction.DataStructures;
using AssetRipper.DocExtraction.MetaData;
using AssetRipper.Primitives;

namespace AssetRipper.DocExtraction.ConsoleApp;

public static class ConsolidatedExtractor
{
    private static readonly UnityVersion MinimumUnityVersion = new UnityVersion(3, 5);

    public static void ExtractAndSave(string inputDirectory, string outputPath)
    {
        HistoryFile historyFile = new();
        Dictionary<string, ClassHistory> classes = historyFile.Classes;
        Dictionary<string, EnumHistory> enums = historyFile.Enums;
        Dictionary<string, StructHistory> structs = historyFile.Structs;

        foreach (DocumentationFile documentationFile in ExtractAllDocumentation(inputDirectory))
        {
            UnityVersion version = UnityVersion.Parse(documentationFile.UnityVersion);

            ProcessListIntoDictionary<ClassHistory, DataMemberHistory, ClassDocumentation, DataMemberDocumentation>(
                version,
                classes,
                documentationFile.Classes);
            ProcessListIntoDictionary<StructHistory, DataMemberHistory, StructDocumentation, DataMemberDocumentation>(
                version,
                structs,
                documentationFile.Structs);
            ProcessListIntoDictionary<EnumHistory, EnumMemberHistory, EnumDocumentation, EnumMemberDocumentation>(
                version,
                enums,
                documentationFile.Enums);

            Console.WriteLine($"[Consolidated] Processed version {documentationFile.UnityVersion}");
        }

        historyFile.SaveAsJson(outputPath);
        Console.WriteLine($"[ConsolidatedExtractor] Saved consolidated history to {outputPath}");
    }

    private static void ProcessListIntoDictionary<THistory, TMemberHistory, TDocumentation, TMemberDocumentation>(
        UnityVersion version,
        Dictionary<string, THistory> dictionary,
        List<TDocumentation> list)
        where TMemberDocumentation : DocumentationBase, new()
        where TDocumentation : TypeDocumentation<TMemberDocumentation>, new()
        where TMemberHistory : HistoryBase, new()
        where THistory : TypeHistory<TMemberHistory, TMemberDocumentation>, new()
    {
        HashSet<string> processedClasses = new();
        foreach (TDocumentation @class in list)
        {
            string fullName = @class.FullName.ToString();
            if (dictionary.TryGetValue(fullName, out THistory? classHistory))
            {
                classHistory.Add(version, @class);
            }
            else
            {
                classHistory = new();
                classHistory.Initialize(version, @class);
                dictionary.Add(fullName, classHistory);
            }
            processedClasses.Add(fullName);
        }
        foreach ((string fullName, THistory classHistory) in dictionary)
        {
            if (!processedClasses.Contains(fullName))
            {
                classHistory.Add(version, null);
            }
        }
    }

    private static IEnumerable<DocumentationFile> ExtractAllDocumentation(string inputDirectory)
    {
        foreach ((UnityVersion unityVersion, string versionFolder) in GetUnityDirectories(inputDirectory))
        {
            // 定义可能的子路径优先级：先找自定义 Release，再找标准 Editor，最后找根目录
            string[] possibleSubPaths = new[]
            {
                "Release",
                @"Editor\Data\Managed",
                "."
            };

            string engineDllPath = FindFile(versionFolder, "UnityEngine.dll", possibleSubPaths);
            string editorDllPath = FindFile(versionFolder, "UnityEditor.dll", possibleSubPaths);

            // XML 文件通常和 DLL 在一起
            string engineXmlPath = Path.ChangeExtension(engineDllPath, ".xml");
            string editorXmlPath = Path.ChangeExtension(editorDllPath, ".xml");

            // 如果找不到核心 DLL，跳过此版本或仅处理已找到的部分
            if (!File.Exists(engineDllPath) && !File.Exists(editorDllPath))
            {
                Console.WriteLine($"[Warning] Could not find UnityEngine.dll or UnityEditor.dll in {versionFolder}. Skipped.");
                continue;
            }

            yield return DocumentationExtractor.ExtractDocumentation(
                unityVersion.ToString(),
                engineXmlPath,
                editorXmlPath,
                engineDllPath,
                editorDllPath);
        }
    }

    private static string FindFile(string root, string fileName, string[] subPaths)
    {
        foreach (var subPath in subPaths)
        {
            string fullPath = Path.Combine(root, subPath, fileName);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
        // 如果找不到，返回默认的标准路径（即使不存在），让上层逻辑处理或报错信息更明确
        return Path.Combine(root, @"Editor\Data\Managed", fileName);
    }

    private static List<(UnityVersion, string)> GetUnityDirectories(string inputDirectory)
    {
        List<(UnityVersion, string)> list = new();
        if (!Directory.Exists(inputDirectory))
        {
            Console.WriteLine($"[Error] Input directory {inputDirectory} does not exist.");
            return list;
        }

        foreach (string versionFolder in Directory.GetDirectories(inputDirectory))
        {
            if (!UnityVersion.TryParse(Path.GetFileName(versionFolder), out UnityVersion unityVersion, out _))
                continue;

            if (unityVersion < MinimumUnityVersion)
            {
                continue;
            }

            // 处理旧版本的特殊情况 (Mac info.plist)，如果不是 Mac 结构，这里通常不会执行
            // 简单起见，这里假设文件夹名就是版本号，或者已经在上方 Parse 成功
            // 如果确实需要读取 Info.plist 修正版本号，保留原逻辑：
            if (unityVersion.LessThan(4, 5))
            {
                string infoPlistPath = Path.Combine(versionFolder, "Editor/Data/PlaybackEngines/macstandaloneplayer/UnityPlayer.app/Contents/Info.plist");
                if (File.Exists(infoPlistPath))
                    unityVersion = XmlDocumentParser.ExtractUnityVersionFromXml(infoPlistPath);
            }
            else if (unityVersion.LessThan(5))
            {
                string infoPlistPath = Path.Combine(versionFolder, "Editor/Data/PlaybackEngines/macstandalonesupport/Variations/universal_development_mono/UnityPlayer.app/Contents/Info.plist");
                if (File.Exists(infoPlistPath))
                    unityVersion = XmlDocumentParser.ExtractUnityVersionFromXml(infoPlistPath);
            }

            list.Add((unityVersion, versionFolder));
        }
        list.Sort((pair1, pair2) => pair1.Item1.CompareTo(pair2.Item1));
        return list;
    }
}