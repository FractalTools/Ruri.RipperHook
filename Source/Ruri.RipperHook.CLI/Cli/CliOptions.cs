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

    public string? BuildCabMapPath { get; init; }

    public string? CabMapPath { get; init; }

    public string[] LoadTypes { get; init; } = [];

    public string? ExportGlbPath { get; init; }

    public string? ExportSceneMap { get; init; }

    public string? SceneLandmark { get; init; }

    public string? SceneWindow { get; init; }

    public bool NoScripts { get; init; }

    public string? DumpVfsPath { get; init; }

    public string[] VfsTypes { get; init; } = [];

    public string? CabQuery { get; init; }

    public string[] CabRules { get; init; } = [];

    public string[] QueryFields { get; init; } = [];

    public int QueryLimit { get; init; }

    public string? DatasetId { get; init; }

    public string[] DatasetArgs { get; init; } = [];

    public bool DatasetList { get; init; }

    public string? DatasetOut { get; init; }

    public string? ExportStandardBundlePath { get; init; }
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
    public Option<string?> ExportGlb { get; }
    public Option<string?> ExportScene { get; }

    public Option<string?> SceneLandmarkOption { get; }

    public Option<string?> SceneWindowOption { get; }

    public Option<bool> NoScriptsOption { get; }

    public Option<string?> DumpVfs { get; }

    public Option<string[]> VfsTypesOption { get; }

    public Option<string?> CabQueryOption { get; }

    public Option<string[]> CabRuleOption { get; }

    public Option<string[]> QueryFieldsOption { get; }

    public Option<int> QueryLimitOption { get; }

    public Option<string?> DataOption { get; }

    public Option<string[]> DataArgOption { get; }

    public Option<bool> DataListOption { get; }

    public Option<string?> DataOutOption { get; }

    public Option<string?> ExportStandardBundleOption { get; }

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
                const RegexOptions options = RegexOptions.IgnoreCase;
                List<Regex> items = new();
                if (result.Tokens.Count == 1 && File.Exists(result.Tokens[0].Value))
                {
                    foreach (string line in File.ReadLines(result.Tokens[0].Value))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { items.Add(new Regex(line, options)); }
                        catch (ArgumentException) { }
                    }
                }
                else
                {
                    foreach (var token in result.Tokens)
                    {
                        try { items.Add(new Regex(token.Value, options)); }
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
        ExportGlb = new Option<string?>("--export-glb", "Write each loaded prefab hierarchy as a complete .glb into this directory (skeleton/materials/morphs/animations; humanoid muscles baked). --names filters prefabs. Directory is never deleted.");
        ExportScene = new Option<string?>("--export-scene", "Export one VFS streaming map (e.g. base01_lv002): placements → best-LOD → CAB closure → Unity-project export + ruri_scene_placements.json manifest. Needs --load <gameRoot> --cab-map --export and a VFS-game --hook.");
        SceneLandmarkOption = new Option<string?>("--scene-landmark", "With --export-scene, export only one named place of the map, at the size the game gives it: <levelId>[,<scale>[,<sceneStateId>...]], e.g. map01_lv007 or map01_lv007,1.5,0. Omit for the whole map.");
        SceneWindowOption = new Option<string?>("--scene-window", "The same window as a world rect instead of a place name: <minX>,<minZ>,<maxX>,<maxZ>[,<sceneStateId>...].");
        NoScriptsOption = new Option<bool>("--no-scripts", "Do not load scripts at all (AssetRipper ScriptContentLevel.Level0). An IL2Cpp game recovers tens of thousands of MonoScripts nothing downstream reads.");
        DumpVfs = new Option<string?>("--dump-vfs", "Dump raw VFS files (including non-bundle payloads AssetRipper never sees) into this directory and exit. --names filters by file name; needs --load <gameRoot> and a VFS-game --hook.");
        VfsTypesOption = new Option<string[]>("--vfs-types", "With --dump-vfs, keep only these VFS block types (e.g. Table JsonData Lua). These are VFS categories, not AssetRipper ClassIDs.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        CabQueryOption = new Option<string?>("--cab-query",
            "Search a CABMap and print the matching container paths as TSV -- map only, nothing is "
            + "decrypted, loaded or exported. Empty text lists everything the rules keep. "
            + "Needs --cab-map <file>.");
        CabRuleOption = new Option<string[]>("--cab-rule",
            "Narrow --cab-query with the browser's own rule grammar: field|relation|value[|exclude], "
            + "repeatable (e.g. extension|is|fbx type_names|contains|AnimationClip).")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        QueryFieldsOption = new Option<string[]>("--query-fields",
            "Columns to print, comma-separated. --cab-query takes cab/container/folder/leaf/stem/"
            + "extension/type_names/source/bundle/deps; --data takes that dataset's own columns.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        QueryLimitOption = new Option<int>("--query-limit", () => 200,
            "Cap the printed rows of --cab-query / --data (0 = all). The reported total is always the real one.");
        DataOption = new Option<string?>("--data",
            "Run a published dataset over --cab-map and print it as TSV -- the same read the Blender "
            + "panels do, with no bundle load. Arguments go through --data-arg.");
        DataArgOption = new Option<string[]>("--data-arg",
            "One dataset argument as name=value, repeatable (e.g. --data-arg channel=cutscene).")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        DataListOption = new Option<bool>("--data-list",
            "List every dataset the enabled --hook publishes, with its role, arguments and description.");
        ExportStandardBundleOption = new Option<string?>("--export-standard-bundle",
            "Rewrite what --load opened as a stock uncompressed UnityFS AssetBundle at this path. "
            + "Assets are re-serialized from the hook's type tree into the official layout, so vanilla "
            + "readers accept them. Narrow it with --types.");
        DataOutOption = new Option<string?>("--data-out",
            "Write a blob dataset's bytes to this file instead of printing a table "
            + "(e.g. --data core.bundle.standard --data-arg bundle=<path> --data-out out.ab).");
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
            ExportGlb,
            ExportScene,
            SceneLandmarkOption,
            SceneWindowOption,
            NoScriptsOption,
            DumpVfs,
            VfsTypesOption,
            CabQueryOption,
            CabRuleOption,
            QueryFieldsOption,
            QueryLimitOption,
            DataOption,
            DataArgOption,
            DataListOption,
            DataOutOption,
            ExportStandardBundleOption,
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
            ExportGlbPath = pr.GetValueForOption(ExportGlb),
            ExportSceneMap = pr.GetValueForOption(ExportScene),
            SceneLandmark = pr.GetValueForOption(SceneLandmarkOption),
            SceneWindow = pr.GetValueForOption(SceneWindowOption),
            NoScripts = pr.GetValueForOption(NoScriptsOption),
            DumpVfsPath = pr.GetValueForOption(DumpVfs),
            VfsTypes = pr.GetValueForOption(VfsTypesOption) ?? [],
            CabQuery = pr.GetValueForOption(CabQueryOption),
            CabRules = pr.GetValueForOption(CabRuleOption) ?? [],
            QueryFields = pr.GetValueForOption(QueryFieldsOption) ?? [],
            QueryLimit = pr.GetValueForOption(QueryLimitOption),
            DatasetId = pr.GetValueForOption(DataOption),
            DatasetArgs = pr.GetValueForOption(DataArgOption) ?? [],
            DatasetOut = pr.GetValueForOption(DataOutOption),
            ExportStandardBundlePath = pr.GetValueForOption(ExportStandardBundleOption),
            DatasetList = pr.GetValueForOption(DataListOption),
            Passthrough = pr.GetValueForArgument(Passthrough) ?? [],
        };
    }
}
