using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Collections.Generic;
using System.Linq;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.RipperHook;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Core.Install;

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
        config.Modules.AddRange(opts.Modules);
        Bootstrap.LoadModules(config);
        foreach (string id in opts.Hooks)
        {
            config.EnabledHooks.Add(id);
        }
        string gameRoot = opts.LoadPaths.Length > 0 ? opts.LoadPaths[0] : string.Empty;
        if (gameRoot.Length > 0 && !opts.Hooks.Any(static id => HookCatalog.DecoderById(id) is not null))
        {
            AddInstallDecoder(config, gameRoot);
        }
        config.EnabledHooks.Add("SkipStreamingAssetsCopy");
        if (opts.LoadTypes.Length > 0)
        {
            config.EnabledHooks.Add("TypeFilterExport");
        }
        Console.Error.WriteLine($"[Ruri.CLI] hooks: {string.Join(", ", config.EnabledHooks)}");
        Data.CoreDatasets.Register();
        Data.Session.SetOptions(ParseSourceOptions(opts.SourceOptions));
        Bootstrap.ApplyHooks(config);
        Data.Session.Open(gameRoot, config.EnabledHooks);
    }

    /// <summary>
    /// Read which game the folder holds and enable that game's decoder, the same way every other
    /// host does it (<see cref="InstallProbe.ResolveDecoder"/>). An install whose identity no
    /// decoder claims is stated, not passed over: reading a decoded game without its decoder does
    /// not fail, it quietly answers with a fraction of the assets.
    /// </summary>
    private static void AddInstallDecoder(HookConfig config, string gameRoot)
    {
        PlayerIdentity? project;
        try
        {
            project = InstallProbe.Project(gameRoot);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Ruri.CLI] cannot read the install at '{gameRoot}': "
                + $"{exception.GetType().Name}: {exception.Message}");
            return;
        }
        if (project is null)
        {
            Console.Error.WriteLine($"[Ruri.CLI] '{gameRoot}' holds no player -- no decoder resolved. "
                + "Point --load at the folder that contains <Product>_Data, or name one with --hook.");
            return;
        }
        string decoder = InstallProbe.ResolveDecoder(gameRoot);
        Console.Error.WriteLine($"[Ruri.CLI] install: product={project.Product} version={project.GameVersion} "
            + $"engine={project.EngineVersion} -> decoder={(decoder.Length > 0 ? decoder : "(none)")}");
        if (decoder.Length > 0)
        {
            config.EnabledHooks.Add(decoder);
            return;
        }
        IReadOnlyList<DecoderHook> known = HookCatalog.VersionsOf(project.Product);
        if (known.Count > 0)
        {
            Console.Error.WriteLine($"[Ruri.CLI] no decoder claims {project.Product} {project.GameVersion}; "
                + $"this build ships decoders for: {string.Join(", ", known.Select(static hook => hook.Id))}. "
                + "Reading it without one yields a fraction of the assets.");
        }
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
