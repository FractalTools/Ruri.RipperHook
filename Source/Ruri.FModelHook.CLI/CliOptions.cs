using System;
using System.Collections.Generic;

namespace Ruri.FModelHook.CLI;

// Minimal CLI option bag — parsed by hand so the CLI can boot without
// dragging in System.CommandLine / argparse-style packages. Shader export is
// fully headless: game directory, AES keys, mappings and EGame version all come
// from the --game-config AppSettings snapshot (HeadlessGameConfig); the export
// level is controlled entirely by the flags here (--split-variants / --export-only
// / --skip-global / --archive-filter).
internal sealed class CliOptions
{
    public bool SkipGlobal { get; set; }
    public bool ListHooks { get; set; }
    public bool Help { get; set; }
    public bool? SplitVariants { get; set; } // null = leave persisted setting alone
    public List<string> Hooks { get; } = new();
    // Decompile-only debug mode. When set, the CLI skips launching FModel
    // entirely and just calls DecompilePipeline.Run against the supplied
    // .ushaderlib (its sidecars must already sit next to it). Lets us
    // validate Pass 110 / 180 / 190 / 200 fixes against a single archive
    // without re-running the export side, which for the master 6.8 GB
    // archive takes 10-15 minutes per iteration.
    public string? DecompileOnly { get; set; }
    // Path to an FModel AppSettings(_Debug).json snapshot. The headless mount
    // reads EVERYTHING from it directly — GameDirectory, EGame version, ALL AES
    // main+dynamic keys, the mappings endpoint, Raw/OutputDirectory — via
    // HeadlessGameConfig. No %AppData% install, no FModel host. This is the
    // primary input; if omitted the CLI falls back to the live
    // %AppData%/FModel/AppSettings(_Debug).json.
    public string? GameConfig { get; set; }

    // Accepted for back-compat only — headless is now the DEFAULT (and only)
    // shader-export mode, so this flag is a no-op. A plain `--game-config <json>`
    // runs the headless pipeline; there is no WPF/auto-export path to opt out of.
    public bool Headless { get; set; }
    // Comma/space/semicolon-separated archive-name tokens (substring match).
    // When set, only matching .ushaderbytecode archives are exported — lets a
    // self-test target one small archive instead of the multi-GB master.
    public string? ArchiveFilter { get; set; }
    // Headless: build the cache + sidecars + .ushaderlib but SKIP decompile.
    // The master archive's 261k-shader decompile is a multi-hour job; this
    // populates the full material cache fast so `--decompile-only` can iterate.
    public bool ExportOnly { get; set; }

    // Settings-free direct GLB scene export. When set, the CLI skips FModel boot
    // entirely (like --decompile-only) and constructs a CUE4Parse
    // DefaultFileProvider straight from the flags below, then exports each
    // matching .umap as a self-contained .glb scene (World Partition included).
    // This is both the headless self-test path and a scriptable batch exporter
    // that needs no %AppData% FModel config.
    public bool ExportMapDirect { get; set; }
    // Print every .umap the provider can see (after mounting) and exit. Use to
    // discover map package paths before picking a --map filter.
    public bool ListMaps { get; set; }
    public string? GameDir { get; set; }       // folder containing the Paks (or the game root)
    public string? MappingsPath { get; set; }  // local .usmap
    public string? UeVersion { get; set; }     // EGame enum name, e.g. GAME_UE5_1
    public List<string> MapFilters { get; } = new(); // --map <substring> (repeatable)
    public string? ExportOut { get; set; }     // output directory for the .glb + materials
    public string? Aes { get; set; }           // optional AES main key (0x...)
    public bool WithMaterials { get; set; }    // opt in to material + texture sidecar export (default: geometry + material names only)

    // Settings-free direct UE -> Unity YAML export ("牛头蛇尾"): build a CUE4Parse
    // provider from --game-dir / --ue-version / --mappings / --aes (same as
    // --export-map-direct), walk matching packages, convert each export through
    // the UnityExport mapper registry, and write .asset + .meta. This is the
    // headless self-test loop for the Unity exporter.
    public bool ExportUnity { get; set; }
    public string? UnityVersion { get; set; }            // target Unity version, e.g. 2022.3.0f1 (default 2022.3.0f1)
    public List<string> PackageFilters { get; } = new(); // --package-filter <substring> (repeatable / comma list)
    public int? MaxPackages { get; set; }                // cap packages scanned (self-test throttle)

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
                case "--skip-global":
                    opts.SkipGlobal = true;
                    break;
                case "--split-variants":
                    opts.SplitVariants = true;
                    break;
                case "--no-split-variants":
                    opts.SplitVariants = false;
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
                case "--headless":
                    opts.Headless = true;
                    break;
                case "--archive-filter":
                    if (i + 1 < args.Length) { opts.ArchiveFilter = args[i + 1]; i++; }
                    break;
                case "--export-only":
                    opts.ExportOnly = true;
                    break;
                case "--export-map-direct":
                    opts.ExportMapDirect = true;
                    break;
                case "--list-maps":
                    opts.ListMaps = true;
                    break;
                case "--game-dir":
                    if (i + 1 < args.Length) { opts.GameDir = args[i + 1]; i++; }
                    break;
                case "--mappings":
                    if (i + 1 < args.Length) { opts.MappingsPath = args[i + 1]; i++; }
                    break;
                case "--ue-version":
                    if (i + 1 < args.Length) { opts.UeVersion = args[i + 1]; i++; }
                    break;
                case "--map":
                    if (i + 1 < args.Length) { opts.MapFilters.Add(args[i + 1]); i++; }
                    break;
                case "--export-out":
                    if (i + 1 < args.Length) { opts.ExportOut = args[i + 1]; i++; }
                    break;
                case "--aes":
                    if (i + 1 < args.Length) { opts.Aes = args[i + 1]; i++; }
                    break;
                case "--with-materials":
                    opts.WithMaterials = true;
                    break;
                case "--export-unity":
                    opts.ExportUnity = true;
                    break;
                case "--unity-version":
                    if (i + 1 < args.Length) { opts.UnityVersion = args[i + 1]; i++; }
                    break;
                case "--package-filter":
                    if (i + 1 < args.Length)
                    {
                        foreach (string tok in args[i + 1].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            opts.PackageFilters.Add(tok.Trim());
                        i++;
                    }
                    break;
                case "--max-packages":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int maxPkg)) { opts.MaxPackages = maxPkg; i++; }
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
        "Usage (headless shader export — the default and only shader mode):",
        "  Ruri.FModelHook.CLI.exe --game-config <AppSettings.json>",
        "                          [--skip-global] [--archive-filter <tok,...>]",
        "                          [--split-variants | --no-split-variants] [--export-only]",
        "                          [--hook <id> ...] [--list-hooks]",
        "",
        "Shader export (export level is set entirely by these flags):",
        "  --game-config PATH    FModel AppSettings(_Debug).json snapshot — the headless",
        "                        mount reads GameDirectory, EGame version, ALL AES keys",
        "                        and mappings straight from it. Falls back to the live",
        "                        %AppData%/FModel/AppSettings(_Debug).json if omitted.",
        "  --archive-filter TOK  Only export .ushaderbytecode archives whose name contains",
        "                        TOK (comma/space/semicolon list; substring match).",
        "  --skip-global         Skip the engine-internal Global shader archive.",
        "  --split-variants      Emit EVERY per-stage variant as a sibling .hlsl file.",
        "  --no-split-variants   Keep only the primary variant inline in the .shader (default).",
        "  --export-only         Build cache + sidecars + .ushaderlib but SKIP decompile.",
        "  --decompile-only PATH Skip the export side; just run DecompilePipeline against an",
        "                        existing <basename>.ushaderlib (sidecars must sit next to it).",
        "  --hook <id>           Enable a specific hook id (repeatable). Default: all discovered.",
        "  --list-hooks          Print discovered hook ids and exit.",
        "",
        "GLB scene export (settings-free, skips FModel boot):",
        "  --export-map-direct   Export .umap maps as .glb scenes (World Partition aware).",
        "  --game-dir PATH       Folder containing the game's Paks (or the game root).",
        "  --mappings PATH       Local .usmap mappings file.",
        "  --ue-version NAME     CUE4Parse EGame enum name, e.g. GAME_UE5_1 (required).",
        "  --map SUBSTR          Only export maps whose package path contains SUBSTR",
        "                        (repeatable). Omit to require --list-maps instead.",
        "  --export-out DIR      Output directory for the .glb + materials/textures.",
        "  --aes 0x...           Optional AES main key if the paks are encrypted.",
        "  --with-materials      Also export material JSON + decoded texture PNGs (default:",
        "                        geometry + material names only — bulk texture decode is",
        "                        intermittently crash-prone on large worlds).",
        "  --list-maps           With --export-map-direct: print every .umap and exit.",
        "",
        "UE -> Unity YAML export (settings-free, skips FModel boot):",
        "  --export-unity        Convert UE assets to Unity .asset + .meta YAML (牛头蛇尾).",
        "  --game-dir PATH       Folder containing the game's Paks (or the game root).",
        "  --ue-version NAME     CUE4Parse EGame enum name, e.g. GAME_UE5_1 (required).",
        "  --mappings PATH       Local .usmap mappings file (required for UE5 IoStore).",
        "  --aes 0x...           Optional AES main key if the paks are encrypted.",
        "  --unity-version VER   Target Unity version (default 2022.3.0f1).",
        "  --package-filter SUB  Only convert packages whose path contains SUB",
        "                        (repeatable / comma list). Omit to convert everything.",
        "  --max-packages N      Cap packages scanned (self-test throttle).",
        "  --export-out DIR      Output directory (cleared each run; default TestLoopOutput).",
        "  -h, --help            Print this help and exit.",
        "",
        "All shader-export inputs (game dir, AES main+dynamic keys, mappings, EGame",
        "version, Raw/OutputDirectory) are read from the --game-config AppSettings",
        "snapshot — no GUI run or %AppData% setup required.",
    });
}
