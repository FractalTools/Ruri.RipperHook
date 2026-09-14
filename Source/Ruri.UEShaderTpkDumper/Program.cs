using Ruri.UEShaderTpkDumper.Core;
using Ruri.UEShaderTpkDumper.Emit;
using Ruri.UEShaderTpkDumper.Parser;

namespace Ruri.UEShaderTpkDumper;

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
            outRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Ruri.FModelHook", "EngineUbMetadata"));
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
        var sourceFiles = UeSourceScanner.EnumerateSourceFiles(engine.RootDir).ToList();
        Console.WriteLine($"[tpk] source files: {sourceFiles.Count}");

        EngineFacts facts = EngineFacts.Read(engine.RootDir, sourceFiles);
        Console.WriteLine($"[tpk] engine facts: {facts.UniformBufferBaseTypes.Count} base types, {facts.NumericTypes.Count} numeric types, "
                          + $"align struct/array/pointer = {facts.StructAlignment}/{facts.ArrayElementAlignment}/{facts.PointerAlignment}, "
                          + $"hash = {facts.HashFormula}, usage flags = {facts.UsageFlags.Count}");
        foreach (string missing in facts.Unresolved)
        {
            Console.Error.WriteLine("  [engine-fact-missing] " + missing);
        }
        if (facts.Unresolved.Count > 0 || facts.HashFormula.Length == 0)
        {
            Console.Error.WriteLine("[tpk] this tree does not state every engine fact a seed needs; nothing emitted for it.");
            return;
        }
        var constants = ConstantsCollector.Collect(sourceFiles);
        Console.WriteLine($"[tpk] constants: {constants.Count}");

        var macroTables = MacroTableExpander.Collect(sourceFiles);
        Console.WriteLine($"[tpk] macro tables: {macroTables.Count}");

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

        Dictionary<string, ImplementMapping> implementMap = ImplementStructScanner.ScanAll(sourceFiles);
        Console.WriteLine($"[tpk] IMPLEMENT_*_STRUCT mappings: {implementMap.Count}");

        string outDir = Path.Combine(outRoot, engine.Version.ToString());
        Directory.CreateDirectory(outDir);
        foreach (string stale in Directory.EnumerateFiles(outDir, "*_MetaData.json"))
        {
            File.Delete(stale);
        }
        int emitted = 0;
        var walker = new LayoutWalker(facts, constants, registry, macroTables);
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
            var hashResources = LayoutWalker.ToHashResources(layout, facts);
            int bindingFlags = 1;
            bool hasStaticSlot = false;
            string bindingFlagsName = "Shader";
            string usageFlagsText = string.Empty;
            string emitBindingName = layout.BindingName;
            if (implementMap.TryGetValue(block.CppName, out ImplementMapping impl))
            {
                bindingFlags = impl.BindingFlags;
                hasStaticSlot = impl.HasStaticSlot;
                usageFlagsText = impl.UsageFlags;
                emitBindingName = string.IsNullOrEmpty(impl.ShaderBindingName) ? layout.BindingName : impl.ShaderBindingName;
                bindingFlagsName = bindingFlags switch
                {
                    1 => "Shader",
                    2 => "Static",
                    3 => "StaticAndShader",
                    _ => $"Flags{bindingFlags}",
                };
            }
            int usageFlags = UsageFlagBits(usageFlagsText, facts);
            uint hash = ComputeLayoutHash.Compute(facts.HashFormula, layout.Size, bindingFlags, hasStaticSlot,
                usageFlags, facts.UsageFlags, hashResources);

            layout.BindingName = emitBindingName;
            JsonEmitter.EmitLayout(outDir, layout, hash, bindingFlagsName, facts.HashFormula, usageFlagsText,
                engineVersion: engine.Version.ToString(),
                engineSourcePath: Path.GetRelativePath(engine.RootDir, block.SourceFile).Replace('\\', '/'));
            emitted++;
        }
        Console.WriteLine($"[tpk] emitted {emitted} layout JSONs under {outDir}");

        var classes = ShaderTypeSeedScanner.ScanAll(sourceFiles).ToList();
        int seedCount = ShaderTypeSeedEmitter.Emit(outDir, classes, engine.Version.ToString());
        Console.WriteLine($"[tpk] emitted {seedCount} ShaderType seed JSONs ({classes.Sum(c => c.Fields.Count)} LAYOUT_FIELDs)");

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

        MaterialBufferRecipe? recipe = MaterialUniformBufferScanner.Scan(engine.RootDir);
        if (recipe is null)
        {
            Console.Error.WriteLine("[tpk] material uniform buffer: CreateBufferStruct not found in this tree.");
        }
        else
        {
            int memberCount = MaterialUniformBufferEmitter.Emit(outDir, recipe, engine.Version.ToString());
            Console.WriteLine($"[tpk] material uniform buffer: {memberCount} member(s), "
                              + $"{recipe.TextureParameterTypes.Count} texture kind(s)"
                              + (recipe.Unresolved.Count > 0 ? $", {recipe.Unresolved.Count} UNRESOLVED" : ""));
            foreach (string line in recipe.Unresolved.Take(5))
            {
                Console.Error.WriteLine("  [unresolved] " + line.Trim());
            }
        }

        PreshaderOpcodeSet? opcodes = PreshaderOpcodeScanner.Scan(engine.RootDir);
        if (opcodes is null)
        {
            Console.Error.WriteLine("[tpk] preshader opcodes: no opcode enum found in this tree.");
        }
        else
        {
            int opcodeCount = PreshaderOpcodeEmitter.Emit(outDir, opcodes, engine.Version.ToString());
            Console.WriteLine($"[tpk] preshader opcodes: {opcodeCount} from {opcodes.EnumName}");
        }
    }

    /// <summary>
    /// The usage flags an IMPLEMENT_*_EX line states, as the engine's own enum values them: the
    /// argument is the flag names OR-ed together, each qualified however the source felt like.
    /// </summary>
    private static int UsageFlagBits(string usageFlagsText, EngineFacts facts)
    {
        int bits = 0;
        foreach (string term in usageFlagsText.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = term[(term.LastIndexOf("::", StringComparison.Ordinal) is var scope && scope >= 0 ? scope + 2 : 0)..];
            if (facts.UsageFlags.TryGetValue(name, out int bit))
            {
                bits |= bit;
            }
            else if (name.Length > 0 && name != "None")
            {
                Console.Error.WriteLine($"  [usage-flag-unknown] '{name}' is not a member of the engine's EUsageFlags");
            }
        }
        return bits;
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
