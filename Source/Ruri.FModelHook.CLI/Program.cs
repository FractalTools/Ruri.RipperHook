using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using Ruri.FModelHook;
using Ruri.FModelHook.Utils;
using Ruri.FModelHook.ShaderDecompiler.Headless;
using Ruri.FModelHook.ShaderDecompiler;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.CLI;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        CliOptions opts = CliOptions.Parse(args);
        if (opts.Help)
        {
            Console.WriteLine(CliOptions.HelpText());
            return 0;
        }

        EnsureHookAssembliesLoaded();

        if (opts.ListHooks)
        {
            return RunListHooks();
        }

        if (!string.IsNullOrWhiteSpace(opts.DecompileOnly))
        {
            return RunDecompileOnly(opts.DecompileOnly!, opts);
        }

        if (opts.ExportAssetPaths.Count > 0)
        {
            return RunExportAsset(opts);
        }

        if (opts.FindShaderForMaterialPaths.Count > 0)
        {
            return RunFindShaderForMaterial(opts);
        }

        return RunHeadlessShaderExport(opts);
    }

    private static int RunDecompileOnly(string libraryPath, CliOptions opts)
    {
        if (!File.Exists(libraryPath))
        {
            HookLogger.LogFailure($"[Ruri.FModelHook.CLI] --decompile-only: file not found: {libraryPath}");
            return 1;
        }
        string libDir = Path.GetDirectoryName(Path.GetFullPath(libraryPath))!;
        string libStem = Path.GetFileNameWithoutExtension(libraryPath);
        string outDir = Path.Combine(libDir, "Decompiled", libStem);

        string? unifiedPath = null;
        DirectoryInfo? probe = new(libDir);
        while (probe != null)
        {
            string candidate = Path.Combine(probe.FullName, "UnifiedShaderMetadata.json");
            if (File.Exists(candidate)) { unifiedPath = candidate; break; }
            probe = probe.Parent;
        }

        HookLogger.Log($"[Ruri.FModelHook.CLI] --decompile-only: library={libraryPath}");
        HookLogger.Log($"[Ruri.FModelHook.CLI]                   output={outDir}");
        HookLogger.Log($"[Ruri.FModelHook.CLI]                   unified={(unifiedPath ?? "(none — names will fall back to sidecars)")}");

        try
        {
            bool splitVariants = opts.SplitVariants ?? ShaderDecompilerSettingsAccess.Current.SplitVariantsToHlslFiles;

            HashSet<int>? indexFilter = null;
            string? envFilter = Environment.GetEnvironmentVariable("RURI_SHADER_INDEX_FILTER");
            if (!string.IsNullOrWhiteSpace(envFilter))
            {
                indexFilter = new HashSet<int>();
                foreach (string tok in envFilter.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(tok.Trim(), out int idx)) indexFilter.Add(idx);
                }
                HookLogger.Log($"[Ruri.FModelHook.CLI] --decompile-only: RURI_SHADER_INDEX_FILTER active, {indexFilter.Count} index(es).");
            }

            DecompileSummary summary = DecompilePipeline.Run(new LibraryDecompileOptions
            {
                LibraryPath = libraryPath,
                OutputDirectory = outDir,
                UnifiedMetadataPath = unifiedPath,
                MaterialFilter = opts.MaterialFilter,
                RecreateOutputDirectory = indexFilter == null && string.IsNullOrWhiteSpace(opts.MaterialFilter),
                SplitVariantsToHlslFiles = splitVariants,
                ShaderIndexFilter = indexFilter,
                Log = HookLogger.Log,
                LogError = HookLogger.LogFailure,
            });
            HookLogger.Log($"[Ruri.FModelHook.CLI] --decompile-only: done. shaders={summary.TotalShaders} decompiled={summary.Decompiled} skipped={summary.Skipped} failed={summary.Failed}");
            return summary.Failed > 0 ? 2 : 0;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[Ruri.FModelHook.CLI] --decompile-only: crashed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    private static int RunHeadlessShaderExport(CliOptions opts)
    {
        string? configPath = opts.GameConfig;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if DEBUG
            configPath = Path.Combine(appData, "FModel", "AppSettings_Debug.json");
#else
            configPath = Path.Combine(appData, "FModel", "AppSettings.json");
#endif
        }
        if (!File.Exists(configPath))
        {
            HookLogger.LogFailure($"[Headless] --game-config not found: {configPath}. Pass --game-config <AppSettings.json>.");
            return 2;
        }

        HeadlessGameConfig cfg;
        try
        {
            cfg = HeadlessGameConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[Headless] Failed to parse config {configPath}: {ex.Message}");
            return 2;
        }

        string? filterRaw = !string.IsNullOrWhiteSpace(opts.ArchiveFilter)
            ? opts.ArchiveFilter
            : Environment.GetEnvironmentVariable("RURI_ARCHIVE_NAME_FILTER");
        List<string>? filter = null;
        if (!string.IsNullOrWhiteSpace(filterRaw))
        {
            filter = filterRaw!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            HookLogger.Log($"[Headless] Archive filter: [{string.Join(", ", filter)}]");
        }

        bool splitVariants = opts.SplitVariants ?? ShaderDecompilerSettingsAccess.Current.SplitVariantsToHlslFiles;
        HookLogger.Log($"[Headless] Config: game='{cfg.GameDirectory}' version={cfg.UeVersion} keys={1 + cfg.DynamicKeys.Count} rawData='{cfg.RawDataDirectory}' splitVariants={splitVariants}");

        try
        {
            HeadlessShaderExportRunner.RunResult result = HeadlessShaderExportRunner.Run(new HeadlessShaderExportRunner.Options
            {
                Config = cfg,
                ArchiveNameFilter = filter,
                SkipGlobal = opts.SkipGlobal,
                SplitVariants = splitVariants,
                SkipDecompile = opts.ExportOnly,
                ListArchivesOnly = opts.ListArchives,
                FindAssetSubstring = opts.FindAsset,
                MaterialFilter = opts.MaterialFilter,
                Log = HookLogger.Log,
                LogError = HookLogger.LogFailure,
            });
            HookLogger.LogSuccess($"[Headless] Done. project={result.ProjectName} archives={result.ArchivesProcessed} materials={result.MaterialInterfaces} mappings={result.MappingsLoaded}");
            return result.MappingsLoaded ? 0 : 3;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[Headless] Crashed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    private static int RunFindShaderForMaterial(CliOptions opts)
    {
        string? configPath = opts.GameConfig;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if DEBUG
            configPath = Path.Combine(appData, "FModel", "AppSettings_Debug.json");
#else
            configPath = Path.Combine(appData, "FModel", "AppSettings.json");
#endif
        }
        if (!File.Exists(configPath))
        {
            HookLogger.LogFailure($"[FindShader] --game-config not found: {configPath}. Pass --game-config <AppSettings.json>.");
            return 2;
        }

        HeadlessGameConfig cfg;
        try
        {
            cfg = HeadlessGameConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[FindShader] Failed to parse config {configPath}: {ex.Message}");
            return 2;
        }

        try
        {
            var locations = HeadlessShaderExportRunner.FindShaderArchivesForMaterials(cfg, opts.FindShaderForMaterialPaths, HookLogger.Log, HookLogger.LogFailure);
            int withArchive = locations.Count(l => l.ArchivePaths.Count > 0);
            HookLogger.LogSuccess($"[FindShader] Done. shader-maps-found={locations.Count} with-archive={withArchive}");
            return withArchive > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[FindShader] Crashed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    private static int RunExportAsset(CliOptions opts)
    {
        string? configPath = opts.GameConfig;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if DEBUG
            configPath = Path.Combine(appData, "FModel", "AppSettings_Debug.json");
#else
            configPath = Path.Combine(appData, "FModel", "AppSettings.json");
#endif
        }
        if (!File.Exists(configPath))
        {
            HookLogger.LogFailure($"[ExportAsset] --game-config not found: {configPath}. Pass --game-config <AppSettings.json>.");
            return 2;
        }

        HeadlessGameConfig cfg;
        try
        {
            cfg = HeadlessGameConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[ExportAsset] Failed to parse config {configPath}: {ex.Message}");
            return 2;
        }

        string outputDirectory = string.IsNullOrWhiteSpace(opts.ExportOut)
            ? Path.Combine(AppContext.BaseDirectory, "ExportAssetOutput")
            : opts.ExportOut!;
        HookLogger.Log($"[ExportAsset] {opts.ExportAssetPaths.Count} package(s) -> {outputDirectory}");

        try
        {
            var exportOptions = UnrealExportOptions.Create(EMeshFormat.UEFormat);
            HeadlessShaderExportRunner.ExportAssetResult result = HeadlessShaderExportRunner.ExportAssetPackages(
                cfg,
                opts.ExportAssetPaths,
                outputDirectory,
                exportOptions,
                HookLogger.Log,
                HookLogger.LogFailure);
            HookLogger.LogSuccess($"[ExportAsset] Done. packages-loaded={result.PackagesLoaded} exports-written={result.ExportsWritten} skipped-unsupported={result.ExportsSkippedUnsupported} mappings={result.MappingsLoaded}");
            return result.ExportsWritten > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[ExportAsset] Crashed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    private static int RunListHooks()
    {
        var decoders = Ruri.Hook.Core.HookCatalog.Decoders;
        if (decoders.Count == 0)
        {
            Console.WriteLine("(no hooks discovered)");
            return 1;
        }
        foreach (var decoder in decoders)
        {
            Console.WriteLine($"{decoder.Id,-24} [{decoder.Type.Name}]");
        }
        return 0;
    }

    private static void EnsureHookAssembliesLoaded()
    {
        Ruri.Hook.Core.HookCatalog.DeclareHost(typeof(Ruri.FModelHook.Attributes.FModelHookAttribute));
        _ = typeof(Ruri.FModelHook.GameType);
        _ = typeof(Ruri.FModelHook.ShaderDecompiler.UE_ShaderDecompiler_Hook);
        try { Assembly.Load("Ruri.FModelHook"); } catch {}

        int hookCount = Ruri.Hook.Core.HookCatalog.Decoders.Count;
        HookLogger.Log($"[Ruri.FModelHook.CLI] Hook assemblies loaded — discovered {hookCount} [GameHookAttribute] type(s).");
        if (hookCount == 0)
        {
            HookLogger.LogFailure("[Ruri.FModelHook.CLI] No hooks discovered. Check that Ruri.FModelHook.dll sits next to Ruri.FModelHook.CLI.exe.");
        }
    }

}
