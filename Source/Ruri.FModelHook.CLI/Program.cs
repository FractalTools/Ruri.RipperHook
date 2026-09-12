using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
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

        if (opts.ExportAssetPaths.Count > 0)
        {
            return RunExportAsset(opts);
        }

        if (opts.FindShaderForMaterialPaths.Count > 0)
        {
            return RunFindShaderForMaterial(opts);
        }

        return RunShaderSource(opts);
    }

    /// <summary>The source of the shaders the named materials compiled to, and nothing else.</summary>
    private static int RunShaderSource(CliOptions opts)
    {
        if (opts.MaterialPaths.Count == 0)
        {
            HookLogger.LogFailure("[ShaderSource] Nothing named. Pass --material <package path> (comma-separated / repeatable).");
            return 2;
        }
        if (!TryLoadConfig(opts, "ShaderSource", out HeadlessGameConfig cfg, out int failure))
        {
            return failure;
        }

        bool splitVariants = opts.SplitVariants ?? ShaderDecompilerSettingsAccess.Current.SplitVariantsToHlslFiles;
        string output = string.IsNullOrWhiteSpace(opts.ExportOut)
            ? Path.Combine(cfg.RawDataDirectory, "Shaders")
            : opts.ExportOut!;
        HookLogger.Log($"[ShaderSource] Config: game='{cfg.GameDirectory}' version={cfg.UeVersion} keys={1 + cfg.DynamicKeys.Count} out='{output}' splitVariants={splitVariants}");

        try
        {
            AbstractVfsFileProvider provider = HeadlessMount.MountProvider(cfg, HookLogger.Log, HookLogger.LogFailure, out bool mappingsLoaded);
            ShaderSourceSummary summary = ShaderSourceRun.Execute(new ShaderSourceRequest
            {
                Provider = provider,
                Subjects = opts.MaterialPaths.Select(static path => (IShaderMapSubject)new MaterialSubject(path)).ToList(),
                OutputDirectory = output,
                SplitVariantsToHlslFiles = splitVariants,
                Log = HookLogger.Log,
                LogError = HookLogger.LogFailure,
            });
            HookLogger.LogSuccess($"[ShaderSource] Done. shader-maps={summary.ShaderMaps} decompiled={summary.Decompiled} skipped={summary.Skipped} failed={summary.Failed} mappings={mappingsLoaded}");
            return summary.Failed > 0 ? 2 : mappingsLoaded ? 0 : 3;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[ShaderSource] Crashed: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}");
            return 1;
        }
    }

    /// <summary>The settings snapshot every headless mode mounts through.</summary>
    private static bool TryLoadConfig(CliOptions opts, string lane, out HeadlessGameConfig cfg, out int failure)
    {
        cfg = null!;
        failure = 0;
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
            HookLogger.LogFailure($"[{lane}] --game-config not found: {configPath}. Pass --game-config <AppSettings.json>.");
            failure = 2;
            return false;
        }
        try
        {
            cfg = HeadlessGameConfig.Load(configPath);
            return true;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[{lane}] Failed to parse config {configPath}: {ex.Message}");
            failure = 2;
            return false;
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
            var locations = HeadlessMount.FindShaderArchivesForMaterials(cfg, opts.FindShaderForMaterialPaths, HookLogger.Log, HookLogger.LogFailure);
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
            HeadlessMount.ExportAssetResult result = HeadlessMount.ExportAssetPackages(
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
