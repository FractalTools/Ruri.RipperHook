using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Ruri.Hook.Config;
using Ruri.RipperHook;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.CLI;

internal static class Program
{
    public static int Main(string[] args)
    {
        Bootstrap.InstallAssemblyResolver();

        // What this prints is a game's own text -- character names, table contents -- and the
        // console's default code page cannot carry most of it. Nothing downstream can recover a
        // name that was already replaced by question marks on the way out.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        HeadlessRunner.JsonStdout = Console.Out;
        Console.SetOut(Console.Error);

        var binder = new CliOptionsBinder();
        var root = binder.BuildRoot();

        int exitCode = 0;
        root.SetHandler((CliOptions opts) =>
        {
            exitCode = Dispatch(opts);
        }, binder);

        var parser = new CommandLineBuilder(root).UseDefaults().Build();
        int parseResult = parser.Invoke(args);
        return parseResult != 0 ? parseResult : exitCode;
    }

    private static int Dispatch(CliOptions opts)
    {
        AssetRipper.Import.Logging.Logger.Add(new StderrLogger { MinLevel = opts.LogLevel });
        Bootstrap.LoadDeclaredModules();
        if (opts.ListHooks)
        {
            ApplyHooks(opts);
            return HeadlessRunner.RunListHooks();
        }

        ApplyHooks(opts);

        if (opts.DatasetList)
        {
            return CabQuery.RunDatasetList(HeadlessRunner.JsonStdout);
        }

        if (opts.CabQuery is not null)
        {
            return CabQuery.RunPaths(opts, HeadlessRunner.JsonStdout);
        }

        if (opts.DatasetId is { Length: > 0 })
        {
            return opts.DatasetOut is { Length: > 0 } datasetOut
                ? CabQuery.RunDatasetBlob(opts, datasetOut)
                : CabQuery.RunDataset(opts, HeadlessRunner.JsonStdout);
        }

        if (opts.BuildCabMapPath is { Length: > 0 } buildOut)
        {
            if (opts.LoadPaths.Length == 0)
            {
                Console.Error.WriteLine("[Ruri.CLI] --build-cab-map needs --load <rootDir> to scan.");
                return 1;
            }
            return CabMap.Build(opts.LoadPaths[0], buildOut);
        }

        bool mapSelects = opts.CabMapPath is { Length: > 0 }
            && (opts.LoadTypes.Length > 0 || opts.Names.Length > 0);
        if (opts.LoadPaths.Length == 0 && !mapSelects)
        {
            Console.Error.WriteLine("[Ruri.CLI] --load is required for headless mode (or --cab-map with --load-types / --names). Use the GUI executable for the AssetRipper Web UI, or pass --list-hooks to query hook ids.");
            return 1;
        }

        return HeadlessRunner.Run(opts);
    }

    private static void ApplyHooks(CliOptions opts)
    {
        var config = new HookConfig();
        foreach (string id in opts.Hooks)
        {
            config.EnabledHooks.Add(id);
        }
        config.EnabledHooks.Add("SkipStreamingAssetsCopy");
        if (opts.LoadTypes.Length > 0)
        {
            config.EnabledHooks.Add("TypeFilterExport");
        }
        Console.Error.WriteLine($"[Ruri.CLI] hooks: {string.Join(", ", config.EnabledHooks)}");
        Data.CoreDatasets.Register();
        Data.Session.SetOptions(ParseSourceOptions(opts.SourceOptions));
        config.Modules.AddRange(opts.Modules);
        Bootstrap.ApplyHooks(config);
        Data.Session.Open(opts.LoadPaths.Length > 0 ? opts.LoadPaths[0] : string.Empty, config.EnabledHooks);
    }

    private static Dictionary<string, string> ParseSourceOptions(string[] pairs)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        foreach (string pair in pairs)
        {
            int separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException($"--source-option takes name=value; got '{pair}'.");
            }
            options[pair[..separator]] = pair[(separator + 1)..];
        }
        return options;
    }

}
