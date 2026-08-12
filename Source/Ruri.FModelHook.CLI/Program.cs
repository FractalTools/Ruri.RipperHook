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
using Ruri.FModelHook.Game.SBUE;
using Ruri.FModelHook.Game.SBUE.GlbSceneExport;
using Ruri.FModelHook.Game.SBUE.Headless;
using Ruri.FModelHook.Game.SBUE.ShaderDecompiler;
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

        if (opts.ExportMapDirect || opts.ListMaps)
        {
            return RunExportMapDirect(opts);
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
            var exportOptions = SbueExportOptions.Create(EMeshFormat.UEFormat);
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

    private static int RunExportMapDirect(CliOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.GameDir) || !Directory.Exists(opts.GameDir))
        {
            HookLogger.LogFailure($"[GlbScene] --game-dir missing or not found: {opts.GameDir}");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(opts.UeVersion) || !Enum.TryParse<EGame>(opts.UeVersion, ignoreCase: true, out var game))
        {
            HookLogger.LogFailure($"[GlbScene] --ue-version invalid or missing (e.g. GAME_UE5_1). Got: '{opts.UeVersion}'");
            return 2;
        }
        if (!opts.ListMaps && opts.MapFilters.Count == 0)
        {
            HookLogger.LogFailure("[GlbScene] No --map filter given. Use --list-maps to discover map paths, or pass --map <substring>.");
            return 2;
        }

        try
        {
            var versions = new VersionContainer(game);
            var provider = new DefaultFileProvider(opts.GameDir!, SearchOption.AllDirectories, isCaseInsensitive: true, versions: versions);
            provider.Initialize();

            string aesHex = string.IsNullOrWhiteSpace(opts.Aes)
                ? "0x0000000000000000000000000000000000000000000000000000000000000000"
                : opts.Aes!;
            try
            {
                provider.SubmitKey(new FGuid(), new FAesKey(aesHex));
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[GlbScene] SubmitKey failed (continuing — paks may be unencrypted): {ex.Message}");
            }
            provider.PostMount();

            if (!string.IsNullOrWhiteSpace(opts.MappingsPath))
            {
                if (!File.Exists(opts.MappingsPath))
                {
                    HookLogger.LogFailure($"[GlbScene] --mappings not found: {opts.MappingsPath}");
                    return 2;
                }
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(opts.MappingsPath!);
            }

            try
            {
                provider.LoadVirtualPaths();
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[GlbScene] LoadVirtualPaths failed (continuing): {ex.Message}");
            }

            HookLogger.Log($"[GlbScene] Provider mounted. game={game} files={provider.Files.Count} mappings={(provider.MappingsContainer != null)}");

            var umaps = provider.Files.Keys
                .Where(key => key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (opts.ListMaps)
            {
                HookLogger.Log($"[GlbScene] {umaps.Count} .umap package(s):");
                foreach (var key in umaps)
                {
                    Console.WriteLine("  " + key);
                }
                return 0;
            }

            var selected = umaps
                .Where(key => opts.MapFilters.Any(filter => key.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (selected.Count == 0)
            {
                HookLogger.LogFailure($"[GlbScene] No .umap matched --map filter(s): {string.Join(", ", opts.MapFilters)}");
                return 2;
            }

            var options = SbueExportOptions.Create(EMeshFormat.Gltf2, opts.WithMaterials);
            string outputDirectory = string.IsNullOrWhiteSpace(opts.ExportOut)
                ? Path.Combine(AppContext.BaseDirectory, "GlbSceneExport")
                : opts.ExportOut!;
            if (Directory.Exists(outputDirectory))
            {
                try { Directory.Delete(outputDirectory, recursive: true); }
                catch (Exception ex) { HookLogger.LogFailure($"[GlbScene] could not clear output dir (continuing): {ex.Message}"); }
            }
            Directory.CreateDirectory(outputDirectory);

            int exported = 0;
            foreach (var key in selected)
            {
                try
                {
                    var package = provider.LoadPackage(provider.Files[key]);
                    UWorld? world = package.GetExports().OfType<UWorld>().FirstOrDefault();
                    if (world == null)
                    {
                        HookLogger.LogFailure($"[GlbScene] '{key}' has no UWorld export; skipped.");
                        continue;
                    }

                    var exporter = new WorldGlbExporter(provider, options, HookLogger.Log, HookLogger.LogFailure);
                    if (exporter.Export(world, key, outputDirectory, CancellationToken.None)) exported++;
                }
                catch (Exception ex)
                {
                    HookLogger.LogFailure($"[GlbScene] '{key}' failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            HookLogger.Log($"[GlbScene] Direct export finished. {exported}/{selected.Count} map(s) exported -> {outputDirectory}");
            return exported > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[GlbScene] Direct export crashed: {ex}");
            return 1;
        }
    }

    private static int RunListHooks()
    {
        var hooks = RuriHook.GetAvailableHooks();
        if (hooks.Count == 0)
        {
            Console.WriteLine("(no hooks discovered)");
            return 1;
        }
        foreach (var (type, attr) in hooks)
        {
            Console.WriteLine($"{attr.GameName}_{attr.Version,-12} [{type.Name}]");
        }
        return 0;
    }

    private static void EnsureHookAssembliesLoaded()
    {
        _ = typeof(Ruri.FModelHook.GameType);
        _ = typeof(Ruri.FModelHook.Game.SBUE.ShaderDecompiler.UE_ShaderDecompiler_Hook);
        try { Assembly.Load("Ruri.FModelHook"); } catch {}

        int hookCount = RuriHook.GetAvailableHooks().Count;
        HookLogger.Log($"[Ruri.FModelHook.CLI] Hook assemblies loaded — discovered {hookCount} [GameHookAttribute] type(s).");
        if (hookCount == 0)
        {
            HookLogger.LogFailure("[Ruri.FModelHook.CLI] No hooks discovered. Check that Ruri.FModelHook.dll sits next to Ruri.FModelHook.CLI.exe.");
        }
    }

}
