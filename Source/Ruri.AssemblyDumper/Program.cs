using AssetRipper.DocExtraction.DataStructures;
using AssetRipper.DocExtraction.MetaData;
using AssetRipper.Primitives;
using System.Diagnostics;
using System.Reflection;

namespace AssetRipper.DocExtraction.ConsoleApp;

internal static class Program
{
    // [Config] 根目录
    private static readonly string RootInputDirectory = @"D:\Ruri\02.Unity\Tools\Unity";

    // [Config] PDB 搜索路径配置
    private static readonly List<(string Path, string Version)> PdbSearchConfigs = new()
    {
        // 2023.2.0x1 Unstripped Release - 包含最全的信息
        (Path.Combine(RootInputDirectory, @"2023.2.0x1\Release"), "2023.2.0x1"),
        // 2021 Editor - 作为补充
        (Path.Combine(RootInputDirectory, @"2021.3.39f1\Editor"), "2021.3.39f1")
    };

    // [Config] 核心 PDB 白名单 (忽略 mono, bee, il2cpp 等无关文件)
    // 注意：文件名不区分大小写，不需要后缀
    private static readonly HashSet<string> PriorityPdbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unity_x64",          // 核心编辑器/引擎 (Windows)
        "Unity",              // 旧版本或某些构建的核心名称
        "UnityPlayer",        // 播放器核心
        "UnityPlayer_Win64",  // 64位播放器
        "unity_bridge",       // 编辑器桥接库 (有时包含 Editor 特有枚举)
        "UnityShaderCompiler" // Shader编译器 (可选，保留以防万一)
    };

    static void Main(string[] args)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        // [Hook Mode]
        if (args.Length > 0 && args[0] == "hook")
        {
            Console.WriteLine("Starting Hook Generation...");
            ClassHookGenerator.Generate();
            stopwatch.Stop();
            Console.WriteLine($"Hook Generation finished in {stopwatch.ElapsedMilliseconds} ms");
            return;
        }

        string consolidatedPath = @"consolidated.json";
        string nativeEnumsPath = @"native_enums.json";
        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(consolidatedPath)) ?? Environment.CurrentDirectory;

        // 1. 生成 Native Enums (仅针对核心 PDB)
        GenerateMergedNativeEnums(nativeEnumsPath);

        // 2. 生成 Consolidated Documentation
        Console.WriteLine($"Starting Consolidated Documentation Extraction from: {RootInputDirectory}");
        ConsolidatedExtractor.ExtractAndSave(RootInputDirectory, consolidatedPath);

        // 3. 导出嵌入数据
        ExportAllGeneratedData(outputDirectory);

        stopwatch.Stop();
        Console.WriteLine($"All tasks finished in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static void GenerateMergedNativeEnums(string outputPath)
    {
        Console.WriteLine("Starting Merged Native Enum Extraction (Core PDBs Only)...");

        Dictionary<string, EnumDocumentation> mergedEnums = new();
        string finalVersion = "2023.2.0x1";

        foreach (var config in PdbSearchConfigs)
        {
            if (!Directory.Exists(config.Path))
            {
                Console.WriteLine($"Warning: Directory not found: {config.Path}");
                continue;
            }

            // 获取目录下所有 pdb
            string[] allPdbFiles = Directory.GetFiles(config.Path, "*.pdb", SearchOption.AllDirectories);

            // 过滤白名单
            var targetPdbs = allPdbFiles
                .Where(f => PriorityPdbNames.Any(name => Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Console.WriteLine($"Scanning {config.Path}: Found {targetPdbs.Count} core PDBs (out of {allPdbFiles.Length} total).");

            foreach (string pdbFile in targetPdbs)
            {
                NativeEnumExtractor.MergeFromPdb(pdbFile, mergedEnums);
            }
        }

        if (mergedEnums.Count > 0)
        {
            DocumentationFile docFile = new DocumentationFile
            {
                UnityVersion = finalVersion,
                Enums = mergedEnums.Values.OrderBy(e => e.FullName).ToList(),
                Classes = new(),
                Structs = new()
            };
            docFile.SaveAsJson(outputPath);
            Console.WriteLine($"Successfully generated {outputPath} with {docFile.Enums.Count} enums.");
        }
        else
        {
            Console.WriteLine("[Error] No enums extracted. Check if PDB paths are correct.");
        }
    }

    private static void ExportAllGeneratedData(string outputDirectory)
    {
        try
        {
            var dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AssetRipper.SourceGenerated.dll");
            if (File.Exists(dllPath))
            {
                Assembly.LoadFrom(dllPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not load AssetRipper.SourceGenerated.dll. {ex.Message}");
        }

        ExportGeneratedData("AssetRipper.SourceGenerated.ReferenceAssembliesJsonData", Path.Combine(outputDirectory, "assemblies.json"));
        ExportGeneratedData("AssetRipper.SourceGenerated.EngineAssetsTpkData", Path.Combine(outputDirectory, "engine_assets.tpk"));
    }

    private static void ExportGeneratedData(string typeName, string outputPath)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(typeName, false);
            if (type == null) continue;

            var field = type.GetField("data", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) continue;

            var data = (byte[])field.GetValue(null);
            File.WriteAllBytes(outputPath, data);
            Console.WriteLine($"Exported {Path.GetFileName(outputPath)}");
            return;
        }
        Console.WriteLine($"Could not find type {typeName} to export.");
    }
}