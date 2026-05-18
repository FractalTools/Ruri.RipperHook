using System.Reflection;
using System.Text;
using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using Newtonsoft.Json;

namespace Ruri.RipperHook.CLI;

internal static class HeadlessRunner
{
    /// <summary>
    /// Reference to the *original* stdout captured at startup. Set by Program.Main before
    /// Console.Out is redirected to stderr (to silence third-party Console.WriteLine in
    /// HookLogger and friends). EmitJson writes here directly so stdout is a single clean
    /// JSON line that another process can pipe into a parser.
    /// </summary>
    public static TextWriter JsonStdout { get; set; } = Console.Out;

    public static int Run(CliOptions options)
    {
        ConfigureLogging(options);

        if (options.LoadPaths.Length == 0)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, "Missing --load");
            return 1;
        }

        string[] paths = ResolveLoadPaths(options.LoadPaths);
        if (paths.Length == 0)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"Path not found: {string.Join(", ", options.LoadPaths)}");
            return 1;
        }

        if (options.CabMapPath is { Length: > 0 } cabMapPath)
        {
            if (!File.Exists(cabMapPath))
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"CABMap not found: {cabMapPath}");
                return 1;
            }
            try
            {
                (string baseFolder, var entries) = CabMap.Load(cabMapPath);
                string[] expanded = CabMap.ResolveDeps(baseFolder, entries, paths);
                Console.Error.WriteLine($"[Ruri.CLI] cab-map: {paths.Length} seed(s) → {expanded.Length} files via {entries.Count}-entry map ({cabMapPath})");
                paths = expanded;
            }
            catch (Exception ex)
            {
                EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"Cannot load CABMap '{cabMapPath}': {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        HashSet<int> allowedClassIds = ResolveTypes(options.Types);
        if (options.Types.Length > 0 && allowedClassIds.Count == 0)
        {
            EmitJson(SummaryStatus.Error, options, 0, new(), 0, [], null, $"No --types resolved (got: {string.Join(",", options.Types)})");
            return 1;
        }

        ExportFilter.Configure(allowedClassIds, options.Names, options.SmokeTestLimit, options.FailFast);
        ExportFilter.Install();

        try
        {
            var settings = new FullConfiguration();
            settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
            settings.LogConfigurationValues();

            var handler = new ExportHandler(settings);
            GameData gameData = handler.Load(paths, LocalFileSystem.Instance);
            handler.Process(gameData);

            (int totalAssets, Dictionary<int, int> byType) = SummarizeAssets(gameData);

            if (allowedClassIds.Count > 0 && !byType.Keys.Any(k => allowedClassIds.Contains(k)))
            {
                EmitJson(SummaryStatus.Error, options, totalAssets, byType, 0, [], null,
                    $"No assets matched --types after load (loaded class ids: {string.Join(",", byType.Keys)})");
                return 4;
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
            list.Add($"{attr.GameName}_{attr.Version}");
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
        Logger.Add(new FileLogger($"Ruri_Cli_{DateTime.Now:yyyyMMdd_HHmmss}.log"));
        Logger.AllowVerbose = options.LogLevel == LogType.Verbose || options.LogLevel == LogType.Debug;
    }

    private static string[] ResolveLoadPaths(string[] loadPaths)
    {
        // Each --load token can be a single file (we hand it to AssetRipper as-is) or a
        // directory (we recursively expand to its top-level entries — AssetRipper handles the
        // sub-tree itself once a directory is in the input set). Multiple tokens get
        // concatenated so callers can list a few specific .ab bundles without staging.
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
