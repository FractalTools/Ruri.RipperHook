using System;
using System.Collections.Generic;

namespace Ruri.FModelHook.CLI;

internal sealed class CliOptions
{
    public bool SkipGlobal { get; set; }
    public bool ListHooks { get; set; }
    public bool ListArchives { get; set; }
    public string? FindAsset { get; set; }
    public List<string> ExportAssetPaths { get; } = new();
    public List<string> FindShaderForMaterialPaths { get; } = new();
    public string? MaterialFilter { get; set; }
    public bool Help { get; set; }
    public bool? SplitVariants { get; set; }    public List<string> Hooks { get; } = new();
    public string? DecompileOnly { get; set; }
    public string? GameConfig { get; set; }

    public bool Headless { get; set; }
    public string? ArchiveFilter { get; set; }
    public bool ExportOnly { get; set; }

    public string? GameDir { get; set; }    public string? MappingsPath { get; set; }    public string? UeVersion { get; set; }    public string? ExportOut { get; set; }    public string? Aes { get; set; }
    public bool ExportUnity { get; set; }
    public string? UnityVersion { get; set; }    public List<string> PackageFilters { get; } = new();    public int? MaxPackages { get; set; }
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
                case "--list-archives":
                    opts.ListArchives = true;
                    break;
                case "--find-asset":
                    if (i + 1 < args.Length) { opts.FindAsset = args[i + 1]; i++; }
                    break;
                case "--export-asset":
                    if (i + 1 < args.Length)
                    {
                        foreach (string tok in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            opts.ExportAssetPaths.Add(tok);
                        i++;
                    }
                    break;
                case "--find-shader-for-material":
                    if (i + 1 < args.Length)
                    {
                        foreach (string tok in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            opts.FindShaderForMaterialPaths.Add(tok);
                        i++;
                    }
                    break;
                case "--material-filter":
                    if (i + 1 < args.Length) { opts.MaterialFilter = args[i + 1]; i++; }
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
                case "--game-dir":
                    if (i + 1 < args.Length) { opts.GameDir = args[i + 1]; i++; }
                    break;
                case "--mappings":
                    if (i + 1 < args.Length) { opts.MappingsPath = args[i + 1]; i++; }
                    break;
                case "--ue-version":
                    if (i + 1 < args.Length) { opts.UeVersion = args[i + 1]; i++; }
                    break;
                case "--export-out":
                    if (i + 1 < args.Length) { opts.ExportOut = args[i + 1]; i++; }
                    break;
                case "--aes":
                    if (i + 1 < args.Length) { opts.Aes = args[i + 1]; i++; }
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
        "  --list-archives       Mount the provider and print every target archive (name +",
        "                        size, respecting --skip-global/--archive-filter), then exit.",
        "  --find-asset SUBSTR   Mount the provider (full AppSettings key set) and print every",
        "                        file path containing SUBSTR, then exit — no shader export.",
        "  --export-asset PATH   Mount (full AppSettings key set) and directly export the given",
        "                        package path(s) — mesh + material + texture, via the same",
        "                        Exporter FModel's GUI \"Export\" uses. Comma-separated / repeatable.",
        "                        Use with --export-out to set the output directory.",
        "  --find-shader-for-material PATH",
        "                        Mount and report which .ushaderbytecode archive(s) contain the",
        "                        given material's shader-maps (comma-separated / repeatable).",
        "  --material-filter TOK Narrow the shader-export decompile OUTPUT to shader-maps whose",
        "                        material path contains TOK. Additive (doesn't wipe prior output)",
        "                        — combine with --archive-filter for a fast, incremental decompile",
        "                        of one material instead of a whole (possibly huge) archive.",
        "  --split-variants      Emit EVERY per-stage variant as a sibling .hlsl file.",
        "  --no-split-variants   Keep only the primary variant inline in the .shader (default).",
        "  --export-only         Build cache + sidecars + .ushaderlib but SKIP decompile.",
        "  --decompile-only PATH Skip the export side; just run DecompilePipeline against an",
        "                        existing <basename>.ushaderlib (sidecars must sit next to it).",
        "  --hook <id>           Enable a specific hook id (repeatable). Default: all discovered.",
        "  --list-hooks          Print discovered hook ids and exit.",
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
