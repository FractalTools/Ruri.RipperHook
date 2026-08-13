using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using Newtonsoft.Json;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.CLI;

internal static class HeadlessRunner
{
    public static TextWriter JsonStdout { get; set; } = Console.Out;

    public static int Run(CliOptions options)
    {
        ConfigureLogging(options);

        if (options.LoadTypes.Length > 0)
        {
            HashSet<int> exportTypeIds = ResolveTypes(options.LoadTypes);
            Ruri.RipperHook.AR.AR_TypeFilterExport_Hook.TargetClassIds.Clear();
            foreach (int id in exportTypeIds)
            {
                Ruri.RipperHook.AR.AR_TypeFilterExport_Hook.TargetClassIds.Add(id);
            }
        }

        bool mapSelects = options.CabMapPath is { Length: > 0 }
            && (options.LoadTypes.Length > 0 || options.Names.Length > 0);

        if (options.LoadPaths.Length == 0 && !mapSelects)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "Missing --load (or use --cab-map with --load-types / --names)");
            return 1;
        }

        string[] paths = ResolveLoadPaths(options.LoadPaths);
        if (paths.Length == 0 && !mapSelects)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"Path not found: {string.Join(", ", options.LoadPaths)}");
            return 1;
        }

        HashSet<string>? loadFilterFileNames = null;

        if (options.DumpVfsPath is { Length: > 0 } dumpVfsPath)
        {
            if (options.LoadPaths.Length == 0 || !Directory.Exists(Path.Combine(options.LoadPaths[0], "Endfield_Data")))
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "--dump-vfs needs --load <gameRoot> (the directory containing Endfield_Data).");
                return 1;
            }
            try
            {
                Directory.CreateDirectory(dumpVfsPath);
                (int matched, SortedDictionary<string, int> blockTypes) =
                    VfsDumper.Dump(dumpVfsPath, options.Names,
                        options.VfsTypes.Length > 0 ? options.VfsTypes : null);
                Console.WriteLine($"VFS 文件类型直方图({blockTypes.Count} 种):");
                foreach ((string blockType, int count) in blockTypes)
                {
                    Console.WriteLine($"  {count,8}  {blockType}");
                }
                Console.WriteLine($"匹配并落盘 {matched} 个 → {dumpVfsPath}");
                return 0;
            }
            catch (Exception exception)
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"--dump-vfs failed: {exception.Message}");
                return 1;
            }
        }

        List<SceneSeedResolver.Placement>? scenePlacements = null;
        if (options.ExportSceneMap is { Length: > 0 } sceneMapName)
        {
            if (options.CabMapPath is not { Length: > 0 } sceneCabMapPath || !File.Exists(sceneCabMapPath))
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "--export-scene needs --cab-map <existing map>.");
                return 1;
            }
            if (options.LoadPaths.Length == 0 || !Directory.Exists(Path.Combine(options.LoadPaths[0], "Endfield_Data")))
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "--export-scene needs --load <gameRoot> (the directory containing Endfield_Data).");
                return 1;
            }
            if (options.ExportPath is null)
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "--export-scene needs --export <dir>.");
                return 1;
            }
            try
            {
                CabTable table = CabMap.LoadTable(sceneCabMapPath);
                (paths, loadFilterFileNames, scenePlacements) = SceneSeedResolver.Resolve(
                    table, sceneMapName, options.SceneLandmark, options.SceneWindow);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Ruri.CLI] scene resolve stack:\n{ex}");
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"Scene resolve failed for '{sceneMapName}': {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
        else if (options.CabMapPath is { Length: > 0 } cabMapPath)
        {
            if (!File.Exists(cabMapPath))
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"CABMap not found: {cabMapPath}");
                return 1;
            }
            try
            {
                CabTable table = CabMap.LoadTable(cabMapPath);
                HashSet<int>? classIds = null;
                if (options.LoadTypes.Length > 0)
                {
                    classIds = ResolveTypes(options.LoadTypes);
                    if (classIds.Count == 0)
                    {
                        EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"No --load-types resolved (got: {string.Join(",", options.LoadTypes)})");
                        return 1;
                    }
                }

                CabSelection selection = new()
                {
                    NamePatterns = options.Names,
                    ClassIds = classIds,
                    FileScopes = mapSelects
                        ? options.LoadPaths.Where(static s => !string.IsNullOrWhiteSpace(s))
                            .Select(static s => Path.GetFullPath(s)).ToArray()
                        : [],
                    SeedCabNames = mapSelects ? [] : CabMap.ResolveCabsForFiles(table, paths),
                };
                CabClosure closure = selection.Resolve(table);
                loadFilterFileNames = closure.LoadFilterFileNames;
                List<string> il2cpp = CollectIl2CppPaths(options.LoadPaths);
                paths = [.. closure.Files, .. il2cpp];
                Console.Error.WriteLine($"[Ruri.CLI] il2cpp inputs appended: {il2cpp.Count}"
                    + (il2cpp.Count > 0 ? $" ({string.Join(", ", il2cpp)})" : string.Empty));
                Console.Error.WriteLine(
                    $"[Ruri.CLI] cab-map: {options.Names.Length} name(s) + {options.LoadTypes.Length} type(s)"
                    + $"{(selection.FileScopes.Length > 0 ? $" scoped to {selection.FileScopes.Length} path(s)" : string.Empty)}"
                    + $" → {closure.SeedCount} seed CAB(s) → {closure.ClosureCount} CAB(s) / {closure.LoadFilterFileNames.Count} bundle(s)"
                    + $" across {paths.Length} chunk file(s), via {table.Count}-entry map ({cabMapPath})");
            }
            catch (Exception ex)
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"Cannot load CABMap '{cabMapPath}': {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        if (paths.Length == 0)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "Nothing to load (no files matched --load / --load-types).");
            return 1;
        }

        HashSet<int> allowedClassIds = ResolveTypes(options.Types);
        if (options.Types.Length > 0 && allowedClassIds.Count == 0)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"No --types resolved (got: {string.Join(",", options.Types)})");
            return 1;
        }

        Regex[] exportNameFilter = mapSelects || scenePlacements is not null ? [] : options.Names;
        ExportFilter.Configure(allowedClassIds, exportNameFilter, options.SmokeTestLimit, options.FailFast);
        ExportFilter.Install();

        try
        {
            var settings = new FullConfiguration();
            settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
            if (options.NoScripts)
            {
                settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
            }
            settings.LogConfigurationValues();

            var handler = new ExportHandler(settings);

            if (loadFilterFileNames is { Count: > 0 })
            {
                GameBundleHook.LoadIncludeFile = name => loadFilterFileNames.Contains(name);
            }
            GameData gameData;
            try
            {
                gameData = handler.Load(paths, LocalFileSystem.Instance);
            }
            finally
            {
                GameBundleHook.LoadIncludeFile = null;
            }
            handler.Process(gameData);

            (int totalAssets, Dictionary<int, int> byType) = SummarizeAssets(gameData);

            if (allowedClassIds.Count > 0 && !byType.Keys.Any(k => allowedClassIds.Contains(k)))
            {
                EmitJson(SummaryStatus.Error, options, totalAssets, byType, 0, [], null,
                    $"No assets matched --types after load (loaded class ids: {string.Join(",", byType.Keys)})");
                return 4;
            }

            if (options.ExportGlbPath is { Length: > 0 } glbPath)
            {
                (int glbExported, int glbFailed) = Ruri.RipperHook.GlbExporter.GlbBatchExporter.ExportPrefabs(
                    gameData, glbPath, options.Names);
                SummaryStatus glbStatus = glbFailed == 0
                    ? (glbExported > 0 ? SummaryStatus.Ok : SummaryStatus.Error)
                    : SummaryStatus.Partial;
                EmitJson(glbStatus, options, totalAssets, byType, glbExported, [], glbPath,
                    glbExported == 0 ? "No prefab hierarchy matched --names for GLB export." : null);
                return glbStatus == SummaryStatus.Ok ? 0 : (glbExported > 0 ? 2 : 4);
            }

            if (options.ExportPath == null)
            {
                EmitJson(SummaryStatus.Ok, options, totalAssets, byType, 0, [], null, null);
                return 0;
            }

            if (Directory.Exists(options.ExportPath))
            {
                Directory.Delete(options.ExportPath, true);
            }
            Directory.CreateDirectory(options.ExportPath);

            try
            {
                handler.Export(gameData, options.ExportPath, LocalFileSystem.Instance);
            }
            catch (Exception ex) when (options.FailFast)
            {
                if (ExportFilter.Failures.Count == 0)
                {
                    ExportFilter.Failures.Add(new ExportFilter.Failure("(unknown)", null, $"{ex.GetType().Name}: {ex.Message}", ex.ToString()));
                }
            }

            if (scenePlacements is not null)
            {
                SceneSeedResolver.WriteManifest(options.ExportPath, options.ExportSceneMap!, scenePlacements);
            }

            int matchedConsidered = ExportFilter.Considered;
            if (matchedConsidered == 0 && (allowedClassIds.Count > 0 || options.Names.Length > 0))
            {
                EmitJson(SummaryStatus.Error, options, totalAssets, byType, 0, [], options.ExportPath,
                    "Filters matched zero assets.");
                return 4;
            }

            SummaryStatus status = ExportFilter.Failures.Count == 0
                ? SummaryStatus.Ok
                : (options.FailFast ? SummaryStatus.Error : SummaryStatus.Partial);

            EmitJson(status, options, totalAssets, byType, ExportFilter.Exported, ExportFilter.Failures.ToList(), options.ExportPath, null);

            if (status == SummaryStatus.Ok) return 0;
            return options.FailFast ? 1 : 2;
        }
        catch (Exception ex)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0,
                new() { new ExportFilter.Failure("(load/process)", null, $"{ex.GetType().Name}: {ex.Message}", ex.ToString()) },
                options.ExportPath, ex.Message);
            return 1;
        }
    }

    public static int RunListHooks()
    {
        var hooks = Ruri.Hook.RuriHook.GetAvailableHooks();
        var list = new List<string>(hooks.Count);
        foreach (var (_, attr) in hooks)
        {
            list.AddRange(Ruri.Hook.RuriHook.BuildHookIds(attr));
        }

        var summary = new
        {
            status = "ok",
            list_hooks = true,
            hooks = list,
        };
        JsonStdout.WriteLine(JsonConvert.SerializeObject(summary));
        return 3;
    }

    private static void ConfigureLogging(CliOptions options)
    {
        Logger.Clear();
        if (!options.Silent)
        {
            Logger.Add(new StderrLogger { MinLevel = options.LogLevel });
        }
        Logger.Add(new FileLogger(Path.Combine(LocalFileSystem.ExecutingDirectory, $"Ruri_Cli_{DateTime.Now:yyyyMMdd_HHmmss}.log")));
        Logger.AllowVerbose = options.LogLevel == LogType.Verbose || options.LogLevel == LogType.Debug;
    }

    private static string[] ResolveLoadPaths(string[] loadPaths)
    {
        var result = new List<string>();
        foreach (string raw in loadPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Directory.Exists(raw))
            {
                result.AddRange(Directory.GetFiles(raw));
                result.AddRange(Directory.GetDirectories(raw));
            }
            else if (File.Exists(raw))
            {
                result.Add(raw);
            }
        }
        return result.ToArray();
    }

    private static List<string> CollectIl2CppPaths(string[] loadPaths)
    {
        var result = new List<string>();
        foreach (string raw in loadPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string root = Directory.Exists(raw) ? raw : Path.GetDirectoryName(Path.GetFullPath(raw)) ?? string.Empty;
            if (root.Length == 0) continue;

            string assembly = Path.Combine(root, "GameAssembly.dll");
            bool hasDataDir = Directory.Exists(root) && Directory.GetDirectories(root, "*_Data").Length > 0;
            if (File.Exists(assembly) && hasDataDir && !result.Contains(root))
            {
                result.Add(root);
            }
        }
        return result;
    }

    private static HashSet<int> ResolveTypes(string[] typeNames)
    {
        var result = new HashSet<int>();
        if (typeNames.Length == 0) return result;

        Type enumType = typeof(ClassIDType);
        foreach (string name in typeNames)
        {
            if (int.TryParse(name, out int explicitId))
            {
                result.Add(explicitId);
                continue;
            }
            if (Enum.TryParse(enumType, name, ignoreCase: true, out object? value))
            {
                result.Add((int)value!);
            }
            else
            {
                Logger.Warning(LogCategory.None, $"--types: unknown ClassID '{name}'");
            }
        }
        return result;
    }

    private static (int total, Dictionary<int, int> byType) SummarizeAssets(GameData gameData)
    {
        int totalAssets = 0;
        var byType = new Dictionary<int, int>();
        foreach (var collection in gameData.GameBundle.FetchAssetCollections())
        {
            foreach (IUnityObjectBase asset in collection)
            {
                totalAssets++;
                int cid = (int)asset.ClassID;
                byType[cid] = byType.GetValueOrDefault(cid) + 1;
            }
        }
        return (totalAssets, byType);
    }

    private enum SummaryStatus { Ok, Partial, Error }

    private static void EmitJson(
        SummaryStatus status,
        CliOptions options,
        int loaded,
        Dictionary<int, int> byType,
        int exported,
        List<ExportFilter.Failure> failures,
        string? exportPath,
        string? errorMessage)
    {
        var byTypeNamed = new Dictionary<string, int>(byType.Count);
        foreach (var kvp in byType)
        {
            string label = Enum.IsDefined(typeof(ClassIDType), kvp.Key)
                ? ((ClassIDType)kvp.Key).ToString()
                : kvp.Key.ToString();
            byTypeNamed[label] = kvp.Value;
        }

        var payload = new Dictionary<string, object?>
        {
            ["status"] = status.ToString().ToLowerInvariant(),
            ["hooks"] = options.Hooks,
            ["loaded"] = loaded,
            ["by_type"] = byTypeNamed,
            ["exported"] = exported,
            ["failed"] = failures.Select(f => new
            {
                name = f.Name,
                class_id = f.ClassId,
                error = f.Error,
                stack = f.Stack,
            }).ToArray(),
            ["export_path"] = exportPath,
        };
        if (errorMessage != null)
        {
            payload["error"] = errorMessage;
        }

        JsonStdout.WriteLine(JsonConvert.SerializeObject(payload));
    }
}
