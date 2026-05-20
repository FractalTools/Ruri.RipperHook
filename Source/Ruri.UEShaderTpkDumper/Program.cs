using Ruri.UEShaderTpkDumper.Core;
using Ruri.UEShaderTpkDumper.Emit;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper;

// CLI entry point. Default behaviour:
//   * Scan `D:\GameStudy\UE` for first-level subdirs whose names contain a
//     `<X>.<Y>.<Z>` version (e.g. `UnrealEngine-5.4.4-release`).
//   * For each engine found, emit UB layout JSONs to
//     `<Repo>/Source/Ruri.FModelHook/EngineUbMetadata/<X>.<Y>.<Z>/`.
//
// Flags:
//   --ue-root <path>          Override the source root (default D:\GameStudy\UE)
//   --out-root <path>         Override the output root (default = committed
//                             EngineUbMetadata folder)
//   --filter <regex>          Only process engines whose folder matches this
//   --list                    Discover-only, print what would be processed
public static class Program
{
    private const string DefaultUeRoot = @"D:\GameStudy\UE";

    public static int Main(string[] args)
    {
        string ueRoot = DefaultUeRoot;
        string? outRoot = null;
        string? filter = null;
        bool listOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ue-root" when i + 1 < args.Length: ueRoot = args[++i]; break;
                case "--out-root" when i + 1 < args.Length: outRoot = args[++i]; break;
                case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
                case "--list": listOnly = true; break;
                case "-h":
                case "--help": Console.WriteLine(HelpText); return 0;
                default:
                    Console.Error.WriteLine($"Unknown arg: {args[i]}");
                    Console.Error.WriteLine(HelpText);
                    return 2;
            }
        }

        if (outRoot is null)
        {
            // Default: write into the committed EngineUbMetadata folder so the
            // C# tool drops files where the runtime decompiler reads them. The
            // path is relative to the tool's binary location — `<repo>/Source/
            // Ruri.UEShaderTpkDumper/bin/Debug/net8.0/` is `..\..\..\..\Ruri.FModelHook\
            // EngineUbMetadata`.
            outRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Ruri.FModelHook", "EngineUbMetadata"));
        }

        Console.WriteLine($"[tpk] ue-root  = {ueRoot}");
        Console.WriteLine($"[tpk] out-root = {outRoot}");

        var engines = UeSourceScanner.DiscoverEngines(ueRoot).ToList();
        if (filter != null)
        {
            var rx = new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            engines = engines.Where(e => rx.IsMatch(e.OriginalFolderName)).ToList();
        }
        Console.WriteLine($"[tpk] discovered {engines.Count} engine(s):");
        foreach (var e in engines) Console.WriteLine($"  {e.Version}  ({e.OriginalFolderName})");
        if (listOnly) return 0;

        foreach (var engine in engines)
        {
            ProcessEngine(engine, outRoot);
        }
        return 0;
    }

    private static void ProcessEngine(DiscoveredEngine engine, string outRoot)
    {
        Console.WriteLine($"\n=== {engine.Version} ({engine.OriginalFolderName}) ===");
        // 1. Pick the right UBMT integer mapping for this engine version.
        IReadOnlyDictionary<string, int> ubmtTable = UbmtTables.ForVersion(engine.Version.Major, engine.Version.Minor);
        Console.WriteLine($"[tpk] UBMT table: {ubmtTable.Count} entries (RDG_TEXTURE_UAV={ubmtTable["RDG_TEXTURE_UAV"]})");

        // 2. Constants sweep (for array-dim resolution).
        var sourceFiles = UeSourceScanner.EnumerateSourceFiles(engine.RootDir).ToList();
        Console.WriteLine($"[tpk] source files: {sourceFiles.Count}");
        var constants = ConstantsCollector.Collect(sourceFiles);
        Console.WriteLine($"[tpk] constants: {constants.Count}");

        // 3. Macro-table collection — UE's `VIEW_UNIFORM_BUFFER_MEMBER_TABLE`
        //    et al. expand to multiple SHADER_PARAMETER lines inside struct
        //    bodies. Collect them now so the walker can pre-expand each
        //    struct body.
        var macroTables = MacroTableExpander.Collect(sourceFiles);
        Console.WriteLine($"[tpk] macro tables: {macroTables.Count}");

        // 4. Struct block enumeration.
        Dictionary<string, StructBlock> registry = new(StringComparer.Ordinal);
        int blockCount = 0;
        foreach (string file in sourceFiles)
        {
            foreach (StructBlock block in StructBlockParser.ParseFile(file))
            {
                registry.TryAdd(block.CppName, block);
                blockCount++;
            }
        }
        Console.WriteLine($"[tpk] struct blocks: {blockCount} ({registry.Count} unique)");

        // 3b. IMPLEMENT_*_STRUCT scan — provides shader-side binding name +
        // BindingFlags / StaticSlot indicator. The layout hash XOR-folds
        // those flags so getting them wrong shifts every hash for static
        // UBs.
        Dictionary<string, ImplementMapping> implementMap = ImplementStructScanner.ScanAll(sourceFiles);
        Console.WriteLine($"[tpk] IMPLEMENT_*_STRUCT mappings: {implementMap.Count}");

        // 4. Walk each UB-class block, emit JSON. Only UNIFORM_BUFFER_STRUCT and
        //    GLOBAL_SHADER_PARAMETER_STRUCT correspond to actual cooked UBs;
        //    plain SHADER_PARAMETER_STRUCT entries are reusable structs that
        //    only appear via SHADER_PARAMETER_STRUCT_INCLUDE.
        string outDir = Path.Combine(outRoot, engine.Version.ToString());
        Directory.CreateDirectory(outDir);
        int emitted = 0;
        var walker = new LayoutWalker(ubmtTable, constants, registry, macroTables);
        foreach (StructBlock block in registry.Values)
        {
            if (block.Kind == "param") continue;
            LayoutResult layout;
            try { layout = walker.Walk(block); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [walk-fail] {block.CppName}: {ex.Message}");
                continue;
            }
            var hashResources = LayoutWalker.ToHashResources(layout, ubmtTable);
            // BindingFlags + StaticSlot come from the IMPLEMENT_*_STRUCT
            // scan. If a struct never has an IMPLEMENT macro, default to
            // "Shader" — that's the Python generator's fallback too.
            int bindingFlags = 1; // Shader
            bool hasStaticSlot = false;
            string bindingFlagsName = "Shader";
            string emitBindingName = layout.BindingName;
            if (implementMap.TryGetValue(block.CppName, out ImplementMapping impl))
            {
                bindingFlags = impl.BindingFlags;
                hasStaticSlot = impl.HasStaticSlot;
                emitBindingName = string.IsNullOrEmpty(impl.ShaderBindingName) ? layout.BindingName : impl.ShaderBindingName;
                bindingFlagsName = bindingFlags switch
                {
                    1 => "Shader",
                    2 => "Static",
                    3 => "StaticAndShader",
                    _ => $"Flags{bindingFlags}",
                };
            }
            uint hash = ComputeLayoutHash.Compute(layout.Size, bindingFlags, hasStaticSlot, hashResources);

            // LayoutResult.BindingName is the C++ struct name by default;
            // swap in the shader binding name from the IMPLEMENT scan
            // (`"View"`, `"Material"`, etc.) so the file name and JSON
            // `Name` field match what the cooked HLSL actually binds.
            layout.BindingName = emitBindingName;
            JsonEmitter.EmitLayout(outDir, layout, hash, bindingFlagsName, ubmtTable,
                engineVersion: engine.Version.ToString(),
                engineSourcePath: Path.GetRelativePath(engine.RootDir, block.SourceFile).Replace('\\', '/'));
            emitted++;
        }
        Console.WriteLine($"[tpk] emitted {emitted} layout JSONs under {outDir}");

        // 6. ShaderType seed extraction — LAYOUT_FIELD scan over every
        //    FShader-derived class. Emits one seed JSON per class with the
        //    parameter NAMES (source-declaration order). Pass180's
        //    TryReconcileGlobalsCB pairs these names with cook-side real
        //    byte offsets to recover `$Globals` member names.
        var classes = ShaderTypeSeedScanner.ScanAll(sourceFiles).ToList();
        int seedCount = ShaderTypeSeedEmitter.Emit(outDir, classes, engine.Version.ToString());
        Console.WriteLine($"[tpk] emitted {seedCount} ShaderType seed JSONs ({classes.Sum(c => c.Fields.Count)} LAYOUT_FIELDs)");

        // 7. Hash-to-name indexes — three sister tables for ShaderType,
        //    VertexFactoryType, and ShaderPipelineType. Same CityHash math,
        //    different name sources.
        var (shaderTypeNames, vfNames, pipelineNames) = IndexNameCollector.CollectAll(sourceFiles);
        int stCount = HashNameIndexEmitter.Emit(outDir, "_ShaderType",
            "FShaderType::HashedName -> source-recovered class name. "
            + "Populates ShaderTypeName at decompile time when the cooked stableinfo.json left it empty.",
            shaderTypeNames);
        int vfCount = HashNameIndexEmitter.Emit(outDir, "_VertexFactoryType",
            "FVertexFactoryType::HashedName -> source-recovered class name. "
            + "Populates VertexFactoryTypeName at decompile time when the cooked stableinfo.json left it empty.",
            vfNames);
        int pipeCount = HashNameIndexEmitter.Emit(outDir, "_ShaderPipelineType",
            "FShaderPipelineType::HashedName -> source-recovered pipeline name. "
            + "Populates PipelineTypeName at decompile time when the cooked stableinfo.json left it empty.",
            pipelineNames);
        Console.WriteLine($"[tpk] hash-to-name: ShaderType={stCount}, VertexFactoryType={vfCount}, ShaderPipelineType={pipeCount}");
    }

    private const string HelpText = """
        Ruri.UEShaderTpkDumper — extract UE shader uniform-buffer layouts from source.

        usage:
          Ruri.UEShaderTpkDumper [--ue-root <path>] [--out-root <path>]
                                 [--filter <regex>] [--list]

        Discovers UE engine versions under D:\GameStudy\UE\* (default), reads
        BEGIN_*_STRUCT blocks, computes the FRHIUniformBufferLayoutInitializer
        layout hash, and emits per-UB JSON metadata under
          <out-root>/<X.Y.Z>/<UBName>_<LayoutHash:X8>_MetaData.json
        ready for the runtime decompile pipeline to consume.

        --filter accepts a regex applied to the engine's folder name (e.g.
        `5\.4` to only do UE 5.4.x). --list prints the discovery list and
        exits without writing.
        """;
}
