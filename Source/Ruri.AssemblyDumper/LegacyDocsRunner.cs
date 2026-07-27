using AssetRipper.DocExtraction.DataStructures;
using AssetRipper.DocExtraction.MetaData;
using AssetRipper.Primitives;
using System.Reflection;

namespace AssetRipper.DocExtraction.ConsoleApp;

/// <summary>
/// 旧 Program.cs 的 PDB → consolidated.json + native_enums.json 抽取逻辑，docs 模式调用。
/// </summary>
internal static class LegacyDocsRunner
{
    private static readonly string RootInputDirectory = @"D:\Ruri\02.Unity\Tools\Unity";

    private static readonly List<(string Path, string Version)> PdbSearchConfigs = new()
    {
        (System.IO.Path.Combine(RootInputDirectory, @"2023.2.0x1\Release"), "2023.2.0x1"),
        (System.IO.Path.Combine(RootInputDirectory, @"2021.3.39f1\Editor"), "2021.3.39f1"),
    };

    private static readonly HashSet<string> PriorityPdbNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Unity_x64", "Unity", "UnityPlayer", "UnityPlayer_Win64", "unity_bridge", "UnityShaderCompiler",
    };

    public static void RunDocsExtraction(string consolidatedPath, string nativeEnumsPath)
    {
        GenerateMergedNativeEnums(nativeEnumsPath);
        Console.WriteLine($"[Docs] Consolidated extraction from {RootInputDirectory}");
        ConsolidatedExtractor.ExtractAndSave(RootInputDirectory, consolidatedPath);
    }

    public static void ExportEmbeddedTpkAndAssembliesJson(string outputDirectory)
    {
        try
        {
            var dllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AssetRipper.SourceGenerated.dll");
            if (System.IO.File.Exists(dllPath)) Assembly.LoadFrom(dllPath);
        }
        catch (Exception ex) { Console.WriteLine($"[Docs] Could not load AssetRipper.SourceGenerated.dll. {ex.Message}"); }

        ExportGeneratedData("AssetRipper.SourceGenerated.ReferenceAssembliesJsonData",
            System.IO.Path.Combine(outputDirectory, "assemblies.json"));
        ExportGeneratedData("AssetRipper.SourceGenerated.EngineAssetsTpkData",
            System.IO.Path.Combine(outputDirectory, "engine_assets.tpk"));
    }

    private static void GenerateMergedNativeEnums(string outputPath)
    {
        Dictionary<string, EnumDocumentation> mergedEnums = new();
        const string finalVersion = "2023.2.0x1";

        foreach (var config in PdbSearchConfigs)
        {
            if (!System.IO.Directory.Exists(config.Path)) continue;
            string[] all = System.IO.Directory.GetFiles(config.Path, "*.pdb", System.IO.SearchOption.AllDirectories);
            var target = all.Where(f => PriorityPdbNames.Any(n => System.IO.Path.GetFileNameWithoutExtension(f).Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (string pdb in target) NativeEnumExtractor.MergeFromPdb(pdb, mergedEnums);
        }
        if (mergedEnums.Count == 0) { Console.WriteLine("[Docs] No native enums extracted."); return; }
        var docFile = new DocumentationFile { UnityVersion = finalVersion, Enums = mergedEnums.Values.OrderBy(e => e.FullName).ToList(), Classes = new(), Structs = new() };
        docFile.SaveAsJson(outputPath);
        Console.WriteLine($"[Docs] Wrote {outputPath} with {docFile.Enums.Count} enums.");
    }

    private static void ExportGeneratedData(string typeName, string outputPath)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(typeName, false);
            if (type == null) continue;
            var field = type.GetField("data", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null) continue;
            System.IO.File.WriteAllBytes(outputPath, (byte[])field.GetValue(null)!);
            Console.WriteLine($"[Docs] Exported {System.IO.Path.GetFileName(outputPath)}");
            return;
        }
    }
}
