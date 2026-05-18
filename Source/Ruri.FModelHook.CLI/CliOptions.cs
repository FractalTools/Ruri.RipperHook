using System;
using System.Collections.Generic;

namespace Ruri.FModelHook.CLI;

// Minimal CLI option bag — parsed by hand so the CLI can boot without
// dragging in System.CommandLine / argparse-style packages. The flag set
// mirrors the auto-export hook's already-recognised CLI args; everything
// else (game directory, AES keys, mappings, Oodle path) is read from the
// user's persisted FModel UserSettings so a CLI run sees exactly what the
// GUI saw last time.
internal sealed class CliOptions
{
    public bool ShaderOnly { get; set; }
    public bool SkipGlobal { get; set; }
    public bool ShowWindow { get; set; }     // default: hide the WPF main window
    public bool KeepAlive { get; set; }      // default: shutdown app once auto-export done
    public bool ListHooks { get; set; }
    public bool Help { get; set; }
    public int  ReadyTimeoutSec { get; set; } = 600;
    public bool? SplitVariants { get; set; } // null = leave persisted setting alone
    public List<string> Hooks { get; } = new();
    // Decompile-only debug mode. When set, the CLI skips launching FModel
    // entirely and just calls DecompilePipeline.Run against the supplied
    // .ushaderlib (its sidecars must already sit next to it). Lets us
    // validate Pass 110 / 180 / 190 / 200 fixes against a single archive
    // without re-running the export side, which for the master 6.8 GB
    // archive takes 10-15 minutes per iteration.
    public string? DecompileOnly { get; set; }
    // Path to an FModel UserSettings JSON snapshot to install over the
    // live `%AppData%/FModel/AppSettings(_Debug).json` BEFORE the WPF
    // host boots. Necessary when re-targeting a different game between
    // CLI runs: FModel's ApplicationViewModel ctor opens a blocking
    // modal DirectorySelector if `PerDirectory[GameDirectory]` isn't
    // already present — invisible in headless mode and blocks forever.
    // Supply the user's per-game snapshot (e.g. AppSettings_OniValleyDemo.json)
    // and the CLI copies it into place before app.Run() is called.
    public string? GameConfig { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var opts = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    opts.Help = true;
                    break;
                case "--list-hooks":
                    opts.ListHooks = true;
                    break;
                case "--shader-only":
                    opts.ShaderOnly = true;
                    break;
                case "--skip-global":
                    opts.SkipGlobal = true;
                    break;
                case "--show-window":
                    opts.ShowWindow = true;
                    break;
                case "--keep-alive":
                case "--no-quit":
                    opts.KeepAlive = true;
                    break;
                case "--split-variants":
                    opts.SplitVariants = true;
                    break;
                case "--no-split-variants":
                    opts.SplitVariants = false;
                    break;
                case "--ready-timeout-sec":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int v))
                    {
                        opts.ReadyTimeoutSec = Math.Max(10, v);
                        i++;
                    }
                    break;
                case "--hook":
                    if (i + 1 < args.Length)
                    {
                        opts.Hooks.Add(args[i + 1]);
                        i++;
                    }
                    break;
                case "--decompile-only":
                    if (i + 1 < args.Length)
                    {
                        opts.DecompileOnly = args[i + 1];
                        i++;
                    }
                    break;
                case "--game-config":
                    if (i + 1 < args.Length)
                    {
                        opts.GameConfig = args[i + 1];
                        i++;
                    }
                    break;
                default:
                    // Pass-through: forwarded to the hook-side ParseCliArgs so
                    // any future flags it grows are auto-consumed without a
                    // CLI-side update. Unknown flags are not an error.
                    break;
            }
        }
        return opts;
    }

    public static string HelpText() => string.Join(Environment.NewLine, new[]
    {
        "Ruri.FModelHook.CLI - headless driver for the FModel ShaderDecompiler hook.",
        "",
        "Usage:",
        "  Ruri.FModelHook.CLI.exe [--shader-only] [--skip-global] [--show-window]",
        "                          [--keep-alive] [--ready-timeout-sec <int>]",
        "                          [--split-variants | --no-split-variants]",
        "                          [--hook <id> ...] [--list-hooks]",
        "",
        "Options:",
        "  --shader-only         Auto-export shader bytecode libraries only (skip materials).",
        "  --skip-global         Skip the engine-internal Global shader archive.",
        "  --show-window         Show FModel's main window (default: hidden).",
        "  --keep-alive          Don't quit once auto-export finishes (default: quit).",
        "  --ready-timeout-sec N Wait up to N seconds for the provider to mount (default 600).",
        "  --split-variants      Force splitting variants into separate .hlsl files.",
        "  --no-split-variants   Force keeping variants in the .shader file.",
        "  --hook <id>           Enable a specific hook id (repeatable). Default: enable",
        "                        every discovered hook (matches GUI default).",
        "  --decompile-only PATH Skip FModel boot; just run DecompilePipeline against",
        "                        an existing <basename>.ushaderlib (sidecars must sit",
        "                        next to it). Useful for re-iterating decompile-side",
        "                        fixes without re-exporting the archive.",
        "  --game-config PATH    Install a UserSettings JSON snapshot over the live",
        "                        %AppData%/FModel/AppSettings(_Debug).json BEFORE booting",
        "                        FModel. Required when re-targeting a different game",
        "                        between CLI runs: FModel pops a blocking modal if",
        "                        PerDirectory[GameDirectory] is missing, and that modal",
        "                        is invisible in headless mode (hangs forever).",
        "  --list-hooks          Print discovered hook ids and exit.",
        "  -h, --help            Print this help and exit.",
        "",
        "Game directory, AES keys, mappings, and Oodle path are read from the same",
        "FModel UserSettings the GUI uses (RawDataDirectory, GameDirectory, etc.).",
        "Run the GUI once to configure them before headless usage.",
    });
}
