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
        if (opts.ListHooks)
        {
            ApplyHooks(opts);
            return HeadlessRunner.RunListHooks();
        }

        ApplyHooks(opts);

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
            config.EnabledHooks.Add(NormalizeHookId(id));
        }
        config.EnabledHooks.Add("AR_SkipStreamingAssetsCopy_");
        if (opts.LoadTypes.Length > 0)
        {
            config.EnabledHooks.Add("AR_TypeFilterExport_");
        }
        if (opts.ExportGlbPath is { Length: > 0 })
        {
            config.EnabledHooks.Add("AR_GlbExporter_");
        }
        Console.Error.WriteLine($"[Ruri.CLI] hooks: {string.Join(", ", config.EnabledHooks)}");
        Bootstrap.ApplyHooks(config);
    }

    private static string NormalizeHookId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;

        string Canonicalize(string s) => s.Replace('.', '_');

        string target = Canonicalize(id);
        var allHooks = Hook.RuriHook.GetAvailableHooks();
        foreach (var (_, attr) in allHooks)
        {
            foreach (string candidate in Hook.RuriHook.BuildHookIds(attr))
            {
                if (string.Equals(Canonicalize(candidate), target, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            if (string.Equals(attr.GameName, id, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(attr.Version))
                return $"{attr.GameName}_{attr.Version}";
        }
        return id;
    }
}
