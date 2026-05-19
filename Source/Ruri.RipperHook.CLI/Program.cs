using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Ruri.Hook.Config;
using Ruri.RipperHook;

namespace Ruri.RipperHook.CLI;

internal static class Program
{
    public static int Main(string[] args)
    {
        Bootstrap.InstallAssemblyResolver();

        // Capture original stdout for the JSON summary, then point Console.Out at stderr so any
        // third-party Console.WriteLine (HookLogger, AssetRipper progress prints, etc.) doesn't
        // contaminate our JSON line.
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
        // System.CommandLine swallows handler return values when SetHandler is Action-shaped, so
        // we capture exitCode out-of-band and prefer our value over the parser's 0/help fall-through.
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

        if (opts.LoadPaths.Length == 0)
        {
            Console.Error.WriteLine("[Ruri.CLI] --load is required for headless mode. Use the GUI executable for the AssetRipper Web UI, or pass --list-hooks to query hook ids.");
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
        if (opts.Hooks.Length > 0)
        {
            Console.Error.WriteLine($"[Ruri.CLI] hooks: {string.Join(", ", config.EnabledHooks)}");
        }
        Bootstrap.ApplyHooks(config);
    }

    /// <summary>
    /// Hook ids are <c>GameName_Version</c>; AR_* hooks have empty Version which yields a
    /// trailing <c>_</c>. Versions usually contain dots (<c>0.1.1.3</c>) but users frequently type
    /// them with underscores (<c>0_1_1_3</c>) to keep the whole id underscore-separated. Accept
    /// both forms by canonicalizing punctuation before comparing.
    /// </summary>
    private static string NormalizeHookId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;

        string Canonicalize(string s) => s.Replace('.', '_');

        string target = Canonicalize(id);
        var allHooks = Hook.RuriHook.GetAvailableHooks();
        foreach (var (_, attr) in allHooks)
        {
            string canonical = $"{attr.GameName}_{attr.Version}";
            if (string.Equals(Canonicalize(canonical), target, StringComparison.OrdinalIgnoreCase))
                return canonical;
            if (string.Equals(attr.GameName, id, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(attr.Version))
                return canonical;
        }
        return id;
    }
}
