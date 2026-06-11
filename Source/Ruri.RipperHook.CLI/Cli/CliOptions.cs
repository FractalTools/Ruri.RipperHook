using System.CommandLine;
using System.CommandLine.Binding;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.CLI;

internal sealed class CliOptions
{
    public string[] Hooks { get; init; } = [];
    public string[] LoadPaths { get; init; } = [];
    public string? ExportPath { get; init; }
    public bool ListHooks { get; init; }
    public string[] Types { get; init; } = [];
    public Regex[] Names { get; init; } = [];
    public int SmokeTestLimit { get; init; }
    public bool Silent { get; init; }
    public LogType LogLevel { get; init; } = LogType.Info;
    public bool FailFast { get; init; } = true;
    public string[] Passthrough { get; init; } = [];

    /// <summary>
    /// Set to write a CABMap (.bin) for the directory given in <see cref="LoadPaths"/>[0],
    /// then exit. Scanning every file in a big game tree is slow (minutes for Endfield_Data),
    /// so build once and reuse via <see cref="CabMapPath"/>.
    /// </summary>
    public string? BuildCabMapPath { get; init; }

    /// <summary>
    /// When set, the CABMap at this path is loaded and used to resolve the transitive
    /// dependency closure of each file in <see cref="LoadPaths"/>. AR then sees every chk the
    /// seed bundles cross-reference, which is the only way to get a complete character
    /// AnimatorController (its BlendTrees / clip refs live in sibling chks).
    /// </summary>
    public string? CabMapPath { get; init; }

    /// <summary>
    /// With <see cref="CabMapPath"/>, load ONLY the bundles that contain an asset of one of these
    /// ClassID names (plus their transitive dependencies), instead of the whole game. The
    /// "build map then precisely filter" path — e.g. <c>--load-types Shader ComputeShader</c> to
    /// export shaders without loading every chk into memory. May be used without <c>--load</c>.
    /// </summary>
    public string[] LoadTypes { get; init; } = [];
}

internal sealed class CliOptionsBinder : BinderBase<CliOptions>
{
    public Option<string[]> Hook { get; }
    public Option<string[]> Load { get; }
    public Option<string?> Export { get; }
    public Option<bool> ListHooks { get; }
    public Option<string[]> Types { get; }
    public Option<Regex[]> Names { get; }
    public Option<int> SmokeTestLimit { get; }
    public Option<bool> Silent { get; }
    public Option<LogType> LogLevel { get; }
    public Option<bool> FailFast { get; }
    public Option<string?> BuildCabMap { get; }
    public Option<string?> CabMap { get; }
    public Option<string[]> LoadTypes { get; }
    public Argument<string[]> Passthrough { get; }

    public CliOptionsBinder()
    {
        Hook = new Option<string[]>("--hook", "Enable hook by id (GameName_Version). Repeatable.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Load = new Option<string[]>("--load", "Files or directories to load (repeatable). Triggers headless export when set.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Export = new Option<string?>("--export", "Export directory.");
        ListHooks = new Option<bool>("--list-hooks", "List every available hook id and exit (code 3).");
        Types = new Option<string[]>("--types", "Filter to these ClassID names (repeatable; e.g. Shader Texture2D).")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Names = new Option<Regex[]>(
            "--names",
            parseArgument: result =>
            {
                List<Regex> items = new();
                if (result.Tokens.Count == 1 && File.Exists(result.Tokens[0].Value))
                {
                    foreach (string line in File.ReadLines(result.Tokens[0].Value))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { items.Add(new Regex(line, RegexOptions.IgnoreCase)); }
                        catch (ArgumentException) { }
                    }
                }
                else
                {
                    foreach (var token in result.Tokens)
                    {
                        try { items.Add(new Regex(token.Value, RegexOptions.IgnoreCase)); }
                        catch (ArgumentException) { }
                    }
                }
                return items.ToArray();
            },
            isDefault: false,
            description: "Asset name regex filter(s). Single token that is an existing file path is loaded line-by-line.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        SmokeTestLimit = new Option<int>("--smoke-test-limit", () => 0, "Limit export to N assets per matching ClassID (0 = unlimited).");
        Silent = new Option<bool>("--silent", "Suppress non-error log output.");
        LogLevel = new Option<LogType>("--log-level", () => LogType.Info, "Log level threshold (Verbose|Debug|Info|Warning|Error).");
        FailFast = new Option<bool>("--fail-fast", () => true, "Abort on first per-asset export failure (default true).");
        BuildCabMap = new Option<string?>("--build-cab-map", "Build a CABMap (.bin) for --load[0] and exit. Format matches the GUI Asset Browser CABMap.");
        CabMap = new Option<string?>("--cab-map", "Load a CABMap (.bin) and expand each --load entry to its transitive CAB dependencies before handing files to AR.");
        LoadTypes = new Option<string[]>("--load-types", "With --cab-map, load only bundles containing these ClassID names (+ deps), e.g. Shader ComputeShader. Build the map first with --build-cab-map.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        Passthrough = new Argument<string[]>("passthrough", () => [], "Forwarded to AssetRipper Web UI when --load is omitted.");
        Passthrough.Arity = ArgumentArity.ZeroOrMore;
    }

    public RootCommand BuildRoot()
    {
        var root = new RootCommand("Ruri.RipperHook CLI")
        {
            Hook,
            Load,
            Export,
            ListHooks,
            Types,
            Names,
            SmokeTestLimit,
            Silent,
            LogLevel,
            FailFast,
            BuildCabMap,
            CabMap,
            LoadTypes,
            Passthrough,
        };
        return root;
    }

    protected override CliOptions GetBoundValue(BindingContext bindingContext)
    {
        var pr = bindingContext.ParseResult;
        return new CliOptions
        {
            Hooks = pr.GetValueForOption(Hook) ?? [],
            LoadPaths = pr.GetValueForOption(Load) ?? [],
            ExportPath = pr.GetValueForOption(Export),
            ListHooks = pr.GetValueForOption(ListHooks),
            Types = pr.GetValueForOption(Types) ?? [],
            Names = pr.GetValueForOption(Names) ?? [],
            SmokeTestLimit = pr.GetValueForOption(SmokeTestLimit),
            Silent = pr.GetValueForOption(Silent),
            LogLevel = pr.GetValueForOption(LogLevel),
            FailFast = pr.GetValueForOption(FailFast),
            BuildCabMapPath = pr.GetValueForOption(BuildCabMap),
            CabMapPath = pr.GetValueForOption(CabMap),
            LoadTypes = pr.GetValueForOption(LoadTypes) ?? [],
            Passthrough = pr.GetValueForArgument(Passthrough) ?? [],
        };
    }
}
