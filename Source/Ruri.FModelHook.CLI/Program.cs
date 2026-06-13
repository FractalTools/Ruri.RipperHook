using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using Ruri.FModelHook.Game.SBUE.GlbSceneExport;
using Ruri.FModelHook.Game.SBUE.Headless;
using Ruri.FModelHook.Game.SBUE.ShaderDecompiler;
using Ruri.FModelHook.UnityExport;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.CLI;

// Headless console entry. The CLI mounts a CUE4Parse provider directly
// from a --game-config AppSettings snapshot and runs the shader
// export+decompile pipeline with NO FModel WPF host — no Hooks menu, no
// settings dialog, no MainWindow, no dispatcher. (The old auto-export
// path that booted FModel and drove it through a MainWindow.OnLoaded
// detour has been removed; headless is the only mode.)
//
// All native dependencies (CUE4Parse-Natives, Oodle, the NuGet
// dxil-spirv / spirv-cross runtimes) live in the shared FModel bin
// output folder which this CLI also publishes into; running from there
// is the supported invocation.
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

        // Force-load the hook-carrying assembly so RuriHook.GetAvailableHooks()
        // sees every hook even before the user runs --list-hooks. The GUI does
        // the same dance via typeof() pinning + Assembly.Load fallback; we
        // mirror it so the CLI behaviour matches.
        EnsureHookAssembliesLoaded();

        if (opts.ListHooks)
        {
            return RunListHooks();
        }

        // Decompile-only debug path. Skip FModel boot entirely; just run
        // DecompilePipeline against the supplied .ushaderlib. The export
        // side already wrote it on a previous run, plus the .assetinfo /
        // .stableinfo / UnifiedShaderMetadata sidecars next to it.
        if (!string.IsNullOrWhiteSpace(opts.DecompileOnly))
        {
            return RunDecompileOnly(opts.DecompileOnly!);
        }

        // Settings-free GLB scene export. Skips FModel boot entirely and drives
        // CUE4Parse directly, so it needs no %AppData% FModel config — handy for
        // scripted batch export and as the headless self-test for the GLB scene
        // exporter (World Partition aware).
        if (opts.ExportMapDirect || opts.ListMaps)
        {
            return RunExportMapDirect(opts);
        }

        // Settings-free direct UE -> Unity YAML export ("牛头蛇尾"). Builds a
        // CUE4Parse provider straight from flags and runs the UnityExport mapper
        // pipeline — the headless self-test loop for the Unity exporter. No
        // FModel boot, no %AppData% config.
        if (opts.ExportUnity)
        {
            return RunExportUnity(opts);
        }

        // Headless shader export+decompile. Builds a CUE4Parse provider straight
        // from the --game-config AppSettings (all AES dynamic keys + mappings +
        // version) and runs the full export+decompile pipeline with NO FModel
        // WPF host. This is the "直接 CLI + 配置好的设置直接反编译" path the user
        // asked for — no GUI, no dispatcher, no hidden-window mapping race.
        // Headless shader export + decompile is the one and only shader path:
        // build a CUE4Parse provider straight from the --game-config AppSettings
        // (every AES dynamic key + mappings + EGame version) and run the full
        // export+decompile pipeline with NO FModel WPF host. The old no-flag
        // fallback that booted FModel and drove the auto-export hook is gone;
        // `--headless` is implied now, so a plain `--game-config <json>` works.
        return RunHeadlessShaderExport(opts);
    }

    // Decompile-only debug runner. Resolves UnifiedShaderMetadata.json by
    // walking up to the project root (`<RawDataDirectory>/<ProjectName>/UnifiedShaderMetadata.json`,
    // matching what UE_ShaderDecompiler_Hook does). Output lands at
    // `<libraryDir>/Decompiled/<libraryStem>/` so the dump matches the
    // shape produced by the full export+decompile pipeline.
    private static int RunDecompileOnly(string libraryPath)
    {
        if (!File.Exists(libraryPath))
        {
            HookLogger.LogFailure($"[Ruri.FModelHook.CLI] --decompile-only: file not found: {libraryPath}");
            return 1;
        }
        string libDir = Path.GetDirectoryName(Path.GetFullPath(libraryPath))!;
        string libStem = Path.GetFileNameWithoutExtension(libraryPath);
        string outDir = Path.Combine(libDir, "Decompiled", libStem);

        // Resolve UnifiedShaderMetadata.json. The hook writes it under
        // `<RawDataDirectory>/<ProjectName>/UnifiedShaderMetadata.json`.
        // For decompile-only we don't have a CUE4ParseViewModel handy, so
        // walk upwards from the .ushaderlib looking for the file. The
        // export pipeline always sites it at the project-root level.
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
            // SplitVariants flag obeys the persisted setting (loaded by the
            // CLI's WireModuleSettings path normally; here we read the
            // public access shim directly to avoid booting HookConfig).
            bool splitVariants = ShaderDecompilerSettingsAccess.Current.SplitVariantsToHlslFiles;

            // Diagnostic gate: `RURI_SHADER_INDEX_FILTER=1234,5678` limits the
            // pipeline to those shader indices only. Skips the multi-minute
            // full-archive walk so hot-iteration on a target shader is fast.
            // When the env var is absent the pipeline behaves identically to
            // before (full archive). Whitespace-tolerant; ignores non-int.
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
                // Don't wipe existing output when a filter is active —
                // diagnostic re-runs target a single shader, full archive
                // results from prior runs stay intact.
                RecreateOutputDirectory = indexFilter == null,
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

    // Settings-free direct GLB scene export: build a CUE4Parse provider from
    // explicit flags, then export each matching .umap as a .glb scene with full
    // World Partition aggregation. Mirrors --decompile-only in that it never
    // boots FModel's WPF host.
    // Headless shader export+decompile. Reads ALL AES dynamic keys + mappings
    // + EGame version from the --game-config AppSettings (InfinityNikki carries
    // 100+ dynamic keys, so the single-key --aes flag of --export-map-direct is
    // not enough), mounts a CUE4Parse provider directly, and runs the full
    // per-archive pipeline. No FModel WPF host, no dispatcher.
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

        // Archive filter: --archive-filter flag wins, else RURI_ARCHIVE_NAME_FILTER env var.
        string? filterRaw = !string.IsNullOrWhiteSpace(opts.ArchiveFilter)
            ? opts.ArchiveFilter
            : Environment.GetEnvironmentVariable("RURI_ARCHIVE_NAME_FILTER");
        List<string>? filter = null;
        if (!string.IsNullOrWhiteSpace(filterRaw))
        {
            filter = filterRaw!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            HookLogger.Log($"[Headless] Archive filter: [{string.Join(", ", filter)}]");
        }

        // Honour --split-variants / --no-split-variants in headless mode too; fall back to the
        // persisted setting when neither was passed. (The flag was previously wired only into the
        // WPF/auto-export path, so the headless runner silently ignored it and always emitted
        // single-variant — every stage's non-primary permutations were decompiled but elided.)
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

            // Geometry + material names by default; texture sidecar decode is
            // opt-in via --with-materials (it can intermittently hard-crash on
            // large worlds — a race in CUE4Parse's parallel native decode).
            var options = new ExporterOptions { MeshFormat = EMeshFormat.Gltf2, ExportMaterials = opts.WithMaterials };
            string outputDirectory = string.IsNullOrWhiteSpace(opts.ExportOut)
                ? Path.Combine(AppContext.BaseDirectory, "GlbSceneExport")
                : opts.ExportOut!;
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

    // Settings-free direct UE -> Unity YAML export. Builds a CUE4Parse provider
    // from explicit flags (same mount path as --export-map-direct), then converts
    // matching packages to Unity .asset + .meta via the UnityExport mapper. The
    // output directory is wiped each run so the self-test loop never reads a stale
    // mix from a previous iteration.
    private static int RunExportUnity(CliOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.GameDir) || !Directory.Exists(opts.GameDir))
        {
            HookLogger.LogFailure($"[UnityExport] --game-dir missing or not found: {opts.GameDir}");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(opts.UeVersion) || !Enum.TryParse<EGame>(opts.UeVersion, ignoreCase: true, out var game))
        {
            HookLogger.LogFailure($"[UnityExport] --ue-version invalid or missing (e.g. GAME_UE5_1). Got: '{opts.UeVersion}'");
            return 2;
        }

        string outputDirectory = string.IsNullOrWhiteSpace(opts.ExportOut)
            ? Path.Combine(AppContext.BaseDirectory, "RuriUnityExport")
            : opts.ExportOut!;

        try
        {
            var versions = new VersionContainer(game);
            var provider = new DefaultFileProvider(opts.GameDir!, SearchOption.AllDirectories, isCaseInsensitive: true, versions: versions);
            provider.ReadShaderMaps = true;   // materials (Phase 2) need inline shader maps; harmless for textures
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
                HookLogger.LogFailure($"[UnityExport] SubmitKey failed (continuing — paks may be unencrypted): {ex.Message}");
            }
            provider.PostMount();

            if (!string.IsNullOrWhiteSpace(opts.MappingsPath))
            {
                if (!File.Exists(opts.MappingsPath))
                {
                    HookLogger.LogFailure($"[UnityExport] --mappings not found: {opts.MappingsPath}");
                    return 2;
                }
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(opts.MappingsPath!);
            }
            else
            {
                HookLogger.LogFailure("[UnityExport] WARNING: no --mappings given. UE5 IoStore packages use unversioned properties; without a .usmap most assets will fail to deserialize.");
            }

            try { provider.LoadVirtualPaths(); }
            catch (Exception ex) { HookLogger.LogFailure($"[UnityExport] LoadVirtualPaths failed (continuing): {ex.Message}"); }

            HookLogger.Log($"[UnityExport] Provider mounted. game={game} files={provider.Files.Count} mappings={(provider.MappingsContainer != null)} unityVersion={opts.UnityVersion ?? "2022.3.0f1"}");

            // Wipe + recreate the output directory so each self-test run starts clean.
            if (Directory.Exists(outputDirectory))
            {
                try { Directory.Delete(outputDirectory, recursive: true); }
                catch (Exception ex) { HookLogger.LogFailure($"[UnityExport] could not clear output dir (continuing): {ex.Message}"); }
            }
            Directory.CreateDirectory(outputDirectory);

            UnityYamlExportRunner.RunResult result = UnityYamlExportRunner.Run(new UnityYamlExportRunner.Options
            {
                Provider = provider,
                OutputDirectory = outputDirectory,
                UnityVersionText = opts.UnityVersion,
                PackageFilter = opts.PackageFilters.Count > 0 ? opts.PackageFilters : null,
                MaxPackages = opts.MaxPackages,
                Log = HookLogger.Log,
                LogError = HookLogger.LogFailure,
            });

            HookLogger.LogSuccess($"[UnityExport] Done. packages={result.PackagesScanned} exports={result.ExportsSeen} converted={result.Converted} files={result.FilesWritten} -> {outputDirectory}");
            return result.FilesWritten > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[UnityExport] Direct export crashed: {ex}");
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
        // Matches the GUI's belt-and-braces approach. The typeof() pin is
        // enough on most configs but Assembly.Load by name is the
        // canonical resolver fallback if the JIT skips type metadata for
        // an unreferenced type.
        _ = typeof(Ruri.FModelHook.GameType);
        _ = typeof(Ruri.FModelHook.Game.SBUE.ShaderDecompiler.UE_ShaderDecompiler_Hook);
        try { Assembly.Load("Ruri.FModelHook"); } catch { /* logged below if 0 hooks */ }

        int hookCount = RuriHook.GetAvailableHooks().Count;
        HookLogger.Log($"[Ruri.FModelHook.CLI] Hook assemblies loaded — discovered {hookCount} [GameHookAttribute] type(s).");
        if (hookCount == 0)
        {
            HookLogger.LogFailure("[Ruri.FModelHook.CLI] No hooks discovered. Check that Ruri.FModelHook.dll sits next to Ruri.FModelHook.CLI.exe.");
        }
    }

}
