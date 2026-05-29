using AssetRipper.Primitives;
using Ruri.AssemblyDumper.Pipeline;
using System.Diagnostics;

namespace Ruri.AssemblyDumper;

/// <summary>
/// 一键工作流：JSON typetree → AR passes → emit dll → recompile → dotnet build → deploy。
///
/// AR 的所有 pass 行为定制都在 <see cref="ArHooks"/> 用 MonoMod 运行时 hook 实现，
/// AssetRipper.AssemblyDumper 子模块要保持上游干净（CLAUDE.md frozen-area 规则）。
/// const <c>SharedState.AssemblyName</c> 不能 hook，由 <see cref="PostProcess"/> 在 Pass998 之前
/// 改 Module/Assembly/Namespace 等价实现。
///
/// 默认 build 模式：
///   1. 从 typetree json 目录生成新的 <c>type_tree.tpk</c>
///   2. 把 <c>consolidated.json / native_enums.json / engine_assets.tpk / assemblies.json</c>
///      从 0Bins/AssetRipper.AssemblyDumper/{Release|Debug}/ 刷新到 cwd
///   3. 安装 MonoMod hooks（ArHooks 复刻 AR 子模块 9 文件 diff）
///   4. 反射调 AR 的 60+ 个 pass，顺序与 AR Program.cs 1:1
///   5. PostProcess 重命名 AssetRipper.SourceGenerated → Ruri.SourceGenerated
///   6. Pass998 写出 <c>Ruri.SourceGenerated.dll</c>
///   7. AR.AssemblyDumper.Recompiler 反编译到 <c>Source/Ruri.SourceGenerated/Ruri/SourceGenerated</c>
///   8. <c>dotnet build Ruri.SourceGenerated.csproj</c> — 它的 &lt;CopyAfterBuild&gt; 把 dll 同步到
///      <c>Source/Ruri.RipperHook/Libraries/Ruri.SourceGenerated.dll</c>
///
/// 旧模式：<c>hook</c>（ClassHookGenerator）/ <c>docs</c>（PDB → consolidated.json）。
/// </summary>
internal static class Program
{
    private const string DefaultTypeTreeJsonDirectory = @"D:\Ruri\Git\FractalTools\TypeTree\output";

    public static int Main(string[] args)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (args.Length == 1)
            {
                string mode = args[0].ToLowerInvariant();
                if (mode == "docs")
                {
                    return RunDocs();
                }
                if (mode == "hook")
                {
                    return RunHook();
                }
                if (mode == "diag")
                {
                    return RunDiag(DefaultTypeTreeJsonDirectory);
                }
            }

            if (args.Length > 1)
            {
                throw new ArgumentException("Expected either no arguments, a single TypeTree JSON directory path, or the legacy 'docs'/'hook' mode.");
            }

            string typeTreeJsonDirectory = args.Length == 0 ? DefaultTypeTreeJsonDirectory : args[0];
            return RunBuild(typeTreeJsonDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ruri.AssemblyDumper] FATAL: {ex}");
            return 1;
        }
        finally
        {
            sw.Stop();
            Console.WriteLine($"[Ruri.AssemblyDumper] Total: {sw.ElapsedMilliseconds} ms");
        }
    }

    // -----------------------------------------------------------------
    // build (default)
    // -----------------------------------------------------------------

    private static int RunBuild(string typeTreeJsonDirectory)
    {
        string repoRoot = LocateRepoRoot();
        string runDir = AppContext.BaseDirectory;
        string tpkPath = Path.Combine(runDir, "type_tree.tpk");
        string emittedDllPath = Path.Combine(runDir, "Ruri.SourceGenerated.dll");
        string resolvedTypeTreeJsonDirectory = ResolveTypeTreeJsonDirectory(typeTreeJsonDirectory);

        Console.WriteLine($"[Build] repo={repoRoot}");
        Console.WriteLine($"[Build] runDir={runDir}");
        Console.WriteLine($"[Build] typeTreeJsonDir={resolvedTypeTreeJsonDirectory}");

        EnsureRequiredArtifacts(runDir, repoRoot);
        TypeTreeTpkBuilder.WriteFromJsonDirectory(resolvedTypeTreeJsonDirectory, tpkPath);

        Directory.SetCurrentDirectory(runDir);
        new ArAssemblyDumperHook().Initialize();
        PassRunner.RunAllExceptSave(tpkPath);
        PostProcess.RenameAssemblyAndNamespaces();
        PassRunner.RunSave();

        if (!File.Exists(emittedDllPath))
        {
            Console.WriteLine($"[Build] {emittedDllPath} not produced; aborting.");
            return 3;
        }

        string ruriSrcGenDir = Path.Combine(repoRoot, "Source", "Ruri.SourceGenerated");
        string ruriSrcGenSourceTree = Path.Combine(ruriSrcGenDir, "Ruri", "SourceGenerated");
        string version = ReadEmittedAssemblyVersion(emittedDllPath);

        int rcode = RecompileStage.Decompile(emittedDllPath, ruriSrcGenSourceTree, version);
        if (rcode != 0) { Console.WriteLine($"[Build] Recompiler returned {rcode}."); return rcode; }

        // Recompiler emits a generic AssetRipper.SourceGenerated.csproj inside the source tree —
        // delete it so the parent Ruri.SourceGenerated.csproj keeps owning the build.
        Try.Delete(Path.Combine(ruriSrcGenSourceTree, "AssetRipper.SourceGenerated.csproj"));

        string ruriSrcGenCsproj = Path.Combine(ruriSrcGenDir, "Ruri.SourceGenerated.csproj");
        int bcode = RecompileStage.Build(ruriSrcGenCsproj);
        if (bcode != 0) { Console.WriteLine($"[Build] dotnet build returned {bcode}."); return bcode; }

        string finalDll = Path.Combine(repoRoot, "Source", "Ruri.RipperHook", "Libraries", "Ruri.SourceGenerated.dll");
        Console.WriteLine($"[Build] Done. Deployed: {finalDll}");
        return 0;
    }

    /// <summary>
    /// Fast iteration mode: build the tpk and run all AR passes (000-941) only — no Save / decompile
    /// / rebuild. Used to validate the TypeTree version set + hook removal in ~40s instead of minutes.
    /// </summary>
    private static int RunDiag(string typeTreeJsonDirectory)
    {
        string repoRoot = LocateRepoRoot();
        string runDir = AppContext.BaseDirectory;
        string tpkPath = Path.Combine(runDir, "type_tree.tpk");
        string resolved = ResolveTypeTreeJsonDirectory(typeTreeJsonDirectory);

        EnsureRequiredArtifacts(runDir, repoRoot);
        TypeTreeTpkBuilder.WriteFromJsonDirectory(resolved, tpkPath);

        Directory.SetCurrentDirectory(runDir);
        new ArAssemblyDumperHook().Initialize();
        PassRunner.RunAllExceptSave(tpkPath);
        Console.WriteLine("[Diag] All passes completed (no save/decompile).");
        return 0;
    }

    /// <summary>
    /// AR 的 SharedState / Pass039 / Pass557 / Pass558 等会在 cwd 找几个外部资源文件，
    /// 都从 0Bins/AssetRipper.AssemblyDumper/{Release|Debug}/ 刷新过来。
    /// </summary>
    private static void EnsureRequiredArtifacts(string runDir, string repoRoot)
    {
        foreach (string fileName in new[] { "consolidated.json", "native_enums.json", "engine_assets.tpk", "assemblies.json" })
        {
            CopyIntoRunDir(runDir, repoRoot, fileName);
        }
    }

    private static void CopyIntoRunDir(string runDir, string repoRoot, string fileName)
    {
        string target = Path.Combine(runDir, fileName);
        foreach (string probe in new[]
                 {
                      Path.Combine(repoRoot, "AssetRipper", "Source", "0Bins", "AssetRipper.AssemblyDumper", "Release", fileName),
                     Path.Combine(repoRoot, "AssetRipper", "Source", "0Bins", "AssetRipper.AssemblyDumper", "Debug", fileName),
                     Path.Combine(repoRoot, "Source", "Ruri.AssemblyDumper", fileName),
                 })
        {
            if (File.Exists(probe))
            {
                File.Copy(probe, target, overwrite: true);
                Console.WriteLine($"[Build] Refreshed {fileName} from {probe}");
                return;
            }
        }
        Console.WriteLine($"[Build] WARN: {fileName} not found anywhere; downstream passes may fail.");
    }

    private static string ResolveTypeTreeJsonDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("TypeTree JSON directory path is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"TypeTree JSON directory not found: {fullPath}");
        }

        if (directory.GetDirectories().Length > 0)
        {
            throw new ArgumentException($"Input must be the flat TypeTree JSON output directory, not a parent folder: {fullPath}", nameof(path));
        }

        FileInfo[] files = directory.GetFiles();
        if (files.Length == 0)
        {
            throw new ArgumentException($"TypeTree JSON directory is empty: {fullPath}", nameof(path));
        }

        int versionJsonCount = 0;
        foreach (FileInfo file in files)
        {
            if (!file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Input directory must contain only TypeTree version JSON files. Unexpected file: {file.FullName}", nameof(path));
            }

            if (!UnityVersion.TryParse(Path.GetFileNameWithoutExtension(file.Name), out _, out _))
            {
                throw new ArgumentException($"Input directory must contain only Unity-versioned TypeTree JSON files. Unexpected file: {file.FullName}", nameof(path));
            }

            versionJsonCount++;
        }

        if (versionJsonCount == 0)
        {
            throw new ArgumentException($"No TypeTree version JSON files were found in: {fullPath}", nameof(path));
        }

        return fullPath;
    }

    // -----------------------------------------------------------------
    // legacy modes
    // -----------------------------------------------------------------

    private static int RunHook()
    {
        Console.WriteLine("[Hook] ClassHookGenerator (legacy)");
        global::AssetRipper.DocExtraction.ConsoleApp.ClassHookGenerator.Generate();
        return 0;
    }

    private static int RunDocs()
    {
        Console.WriteLine("[Docs] PDB → consolidated.json + native_enums.json");
        const string consolidated = "consolidated.json", nativeEnums = "native_enums.json";
        string outputDir = Path.GetDirectoryName(Path.GetFullPath(consolidated)) ?? Environment.CurrentDirectory;
        global::AssetRipper.DocExtraction.ConsoleApp.LegacyDocsRunner.RunDocsExtraction(consolidated, nativeEnums);
        global::AssetRipper.DocExtraction.ConsoleApp.LegacyDocsRunner.ExportEmbeddedTpkAndAssembliesJson(outputDir);
        return 0;
    }

    // -----------------------------------------------------------------
    // utilities
    // -----------------------------------------------------------------

    private static string ReadEmittedAssemblyVersion(string dllPath)
    {
        try
        {
            var module = AsmResolver.DotNet.ModuleDefinition.FromFile(dllPath);
            return module.Assembly?.Version.ToString() ?? "1.0.0.0";
        }
        catch { return "1.0.0.0"; }
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(dir.FullName, "AssetRipper")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Source")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    private static class Try
    {
        public static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
