using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Collects class / pipeline / vertex-factory NAMES that will get hashed
// into the runtime hash-to-name indexes. Mirrors the Python generator's
// `emit_hash_to_name_index` + sister indexes.
//
// Three name sets:
//   * ShaderType:        FShader-derived class names declared via IMPLEMENT_*_SHADER_TYPE
//                        (incl. ##-expanded specializations) PLUS plain
//                        `class FFoo : public FShader|F*Shader|TGlobalShader<>`.
//   * VertexFactoryType: classes passed as arg[0] to IMPLEMENT_VERTEX_FACTORY_TYPE.
//                        Captures namespace-qualified entries like
//                        `Nanite::FVertexFactory`.
//   * PipelineType:      first arg of IMPLEMENT_SHADERPIPELINE_TYPE_<freq>,
//                        which is a PIPELINE NAME (an identifier), not a class.
public static class IndexNameCollector
{
    // IMPLEMENT_<prefix>SHADER_TYPE(template<>|template<X>, ClassName, ...)
    // Permissive on the prefix so plugin-side wrappers (IMPLEMENT_OCIO_SHADER_TYPE,
    // IMPLEMENT_NIAGARA_SHADER_TYPE, …) all match.
    private static readonly Regex s_shaderTypePattern = new(
        @"\bIMPLEMENT_(?:[A-Z][A-Z0-9_]*_)?SHADER_TYPE\s*\("
        + @"[^,]*,\s*"
        + @"([A-Za-z_][A-Za-z_0-9<>:,\s##]*?)\s*,",
        RegexOptions.Compiled);

    // First-arg variants — IMPLEMENT_GLOBAL_SHADER / IMPLEMENT_RESOLVE_SHADER /
    // *_PIXEL_SHADER / *_VERTEX_SHADER / *_COMPUTE_SHADER / *_RAYTRACING_SHADER /
    // VIRTUALTEXTURE_SHADER_TYPE.
    private static readonly Regex s_firstArgPattern = new(
        @"\bIMPLEMENT_(?:GLOBAL_SHADER|RESOLVE_SHADER|"
        + @"[A-Z_]+_PIXEL_SHADER|[A-Z_]+_VERTEX_SHADER|[A-Z_]+_COMPUTE_SHADER|"
        + @"[A-Z_]+_RAYTRACING_SHADER|VIRTUALTEXTURE_SHADER_TYPE)\s*\(\s*"
        + @"([A-Za-z_][A-Za-z_0-9<>:,\s]*?)\s*[,\)]",
        RegexOptions.Compiled);

    // Numbered/suffixed SHADER_TYPE variants (Shader.h:1543-1593) — class slot
    // varies per macro, so we extract via a tiny parser instead of one regex.
    public static readonly IReadOnlyDictionary<string, int> ShaderTypeVariantSlot = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["IMPLEMENT_SHADER_TYPE2"] = 0,
        ["IMPLEMENT_SHADER_TYPE3"] = 0,
        ["IMPLEMENT_SHADER_TYPE_WITH_DEBUG_NAME"] = 1,
        ["IMPLEMENT_SHADER_TYPE2_WITH_TEMPLATE_PREFIX"] = 1,
        ["IMPLEMENT_SHADER_TYPE4_WITH_TEMPLATE_PREFIX"] = 2,
    };

    private static readonly Regex s_shaderTypeVariantPattern = new(
        @"\b(IMPLEMENT_SHADER_TYPE(?:2|3|_WITH_DEBUG_NAME|2_WITH_TEMPLATE_PREFIX|4_WITH_TEMPLATE_PREFIX))\s*\(",
        RegexOptions.Compiled);

    // Direct `class FFoo : public FShader|F*Shader|TGlobalShader<>` declarations.
    private static readonly Regex s_classDeclPattern = new(
        @"\bclass\s+(?:[A-Z][A-Z0-9_]+_API\s+)?(?<name>[A-Z][A-Za-z0-9_]+)\b"
        + @"\s*(?::|<[^>{}]+>\s*:)\s*public\s+"
        + @"(?:F[A-Z][A-Za-z0-9_]*Shader"
        + @"|TGlobalShader<[^>]+>"
        + @"|TShader<[^>]+>"
        + @"|TGlobalShaderPermutation<[^>]+>)\b",
        RegexOptions.Compiled);

    // IMPLEMENT_VERTEX_FACTORY_TYPE[_EX](FactoryClass, ...). Captures
    // namespace-qualified entries verbatim (`Nanite::FVertexFactory`).
    private static readonly Regex s_vfPattern = new(
        @"\bIMPLEMENT_VERTEX_FACTORY_TYPE(?:_EX)?\s*\(\s*"
        + @"([A-Za-z_][A-Za-z_0-9<>:,\s]*?)\s*[,\)]",
        RegexOptions.Compiled);

    // IMPLEMENT_SHADERPIPELINE_TYPE_<freq>(PipelineName, ...).
    private static readonly Regex s_pipelinePattern = new(
        @"\bIMPLEMENT_SHADERPIPELINE_TYPE_[A-Z]+\s*\(\s*"
        + @"([A-Za-z_][A-Za-z_0-9<>:,\s]*?)\s*[,\)]",
        RegexOptions.Compiled);

    // Param-name SUBSTRINGS that betray a pseudo-invocation: macros wrapping
    // IMPLEMENT_*_SHADER_TYPE whose args are themselves param names of an
    // outer macro. Real FShader names never have these tails.
    private static readonly string[] s_pseudoInvocationTails =
    {
        "PolicyName", "LightName", "LayoutName", "ShaderName", "TypeName",
        "ClassName", "ParamName", "FrequencyName", "PrefixName",
    };

    private static readonly HashSet<string> s_macroParamTokens = new(StringComparer.Ordinal)
    {
        "ShaderClass", "ShaderType", "ClassName", "PSClass", "VSClass",
        "TemplatePrefix", "RequiredAPI", "Class", "Type", "DerivedType",
        "FactoryClass", "VertexFactoryType", "PipelineName", "PipelineType",
    };

    // Match `#define <NAME>[(args)] <body>` blocks so we can SKIP IMPLEMENT_*
    // hits that fall inside another macro's definition (those args are
    // param-name placeholders, not real classes).
    private static readonly Regex s_defineBlockPattern = new(
        @"^[ \t]*#define[ \t]+[A-Za-z_][A-Za-z_0-9]*(?:\([^)]*\))?"
        + @"(?:[ \t]+[^\n]*\\\r?\n(?:[^\n]*\\\r?\n)*[^\n]*"
        + @"|[ \t]+[^\n]*"
        + @")?",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static (HashSet<string> ShaderType, HashSet<string> VertexFactory, HashSet<string> Pipeline) CollectAll(IEnumerable<string> sourceFiles)
    {
        // Cache the file list so the macro-expander second pass doesn't re-
        // enumerate the file system.
        List<string> files = sourceFiles.ToList();
        HashSet<string> shaderTypes = new(StringComparer.Ordinal);
        HashSet<string> vfs = new(StringComparer.Ordinal);
        HashSet<string> pipelines = new(StringComparer.Ordinal);

        // Pre-pass: collect wrapper-macro definitions that `##`-concatenate
        // into IMPLEMENT_*_SHADER_TYPE. Then expand every invocation across
        // the source tree to recover specialised class names like
        // `TLightMapDensityPSFDummyLightMapPolicy` that don't appear in any
        // direct IMPLEMENT_*_SHADER_TYPE call site.
        var macroDefs = ImplementMacroExpander.CollectMacroDefs(files);
        if (macroDefs.Count > 0)
        {
            HashSet<string> expanded = ImplementMacroExpander.ExpandInvocations(macroDefs, files);
            foreach (string n in expanded)
            {
                if (string.IsNullOrEmpty(n) || s_macroParamTokens.Contains(n)) continue;
                if (IsPseudoInvocation(n)) continue;
                shaderTypes.Add(n);
            }
        }

        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Length == 0) continue;
            bool hasShader = text.Contains("SHADER_TYPE", StringComparison.Ordinal)
                          || text.Contains("_SHADER(", StringComparison.Ordinal)
                          || text.Contains("public F", StringComparison.Ordinal);
            bool hasVf = text.Contains("IMPLEMENT_VERTEX_FACTORY_TYPE", StringComparison.Ordinal);
            bool hasPipeline = text.Contains("IMPLEMENT_SHADERPIPELINE_TYPE", StringComparison.Ordinal);
            if (!hasShader && !hasVf && !hasPipeline) continue;

            string stripped = UeSourceScanner.StripComments(text);
            // Precompute define-block ranges so IMPLEMENT_* hits inside macro
            // bodies (with placeholder args) get filtered.
            List<(int Start, int End)> defineRanges = new();
            foreach (Match m in s_defineBlockPattern.Matches(stripped))
            {
                defineRanges.Add((m.Index, m.Index + m.Length));
            }
            bool InDefineBody(int pos) => defineRanges.Any(r => pos >= r.Start && pos < r.End);

            if (hasShader)
            {
                foreach (Match m in s_shaderTypePattern.Matches(stripped))
                {
                    if (InDefineBody(m.Index)) continue;
                    string n = m.Groups[1].Value.Trim();
                    if (n.Contains("##") || string.IsNullOrEmpty(n)) continue;
                    n = Regex.Replace(n, @"\s+", "");
                    if (s_macroParamTokens.Contains(n)) continue;
                    if (IsPseudoInvocation(n)) continue;
                    shaderTypes.Add(n);
                }
                foreach (Match m in s_firstArgPattern.Matches(stripped))
                {
                    if (InDefineBody(m.Index)) continue;
                    string n = m.Groups[1].Value.Trim();
                    if (n.Contains("##") || string.IsNullOrEmpty(n)) continue;
                    if (s_macroParamTokens.Contains(n)) continue;
                    n = Regex.Replace(n, @"\s+", "");
                    if (IsPseudoInvocation(n)) continue;
                    shaderTypes.Add(n);
                }
                foreach (Match m in s_shaderTypeVariantPattern.Matches(stripped))
                {
                    if (InDefineBody(m.Index)) continue;
                    string macroName = m.Groups[1].Value;
                    string? extracted = ExtractVariantClass(stripped, m, macroName);
                    if (extracted is null) continue;
                    string n = Regex.Replace(extracted, @"\s+", "");
                    if (n.Length == 0 || !"FTUIAC".Contains(n[0])) continue;
                    if (s_macroParamTokens.Contains(n)) continue;
                    if (IsPseudoInvocation(n)) continue;
                    shaderTypes.Add(n);
                }
                foreach (Match m in s_classDeclPattern.Matches(stripped))
                {
                    string n = m.Groups["name"].Value.Trim();
                    if (string.IsNullOrEmpty(n) || s_macroParamTokens.Contains(n)) continue;
                    if (IsPseudoInvocation(n)) continue;
                    shaderTypes.Add(n);
                }
            }

            if (hasVf)
            {
                foreach (Match m in s_vfPattern.Matches(stripped))
                {
                    if (InDefineBody(m.Index)) continue;
                    string n = m.Groups[1].Value.Trim();
                    if (n.Contains("##") || s_macroParamTokens.Contains(n)) continue;
                    n = Regex.Replace(n, @"\s+", "");
                    if (n.Length > 0) vfs.Add(n);
                }
            }

            if (hasPipeline)
            {
                foreach (Match m in s_pipelinePattern.Matches(stripped))
                {
                    if (InDefineBody(m.Index)) continue;
                    string n = m.Groups[1].Value.Trim();
                    if (n.Contains("##") || s_macroParamTokens.Contains(n)) continue;
                    n = Regex.Replace(n, @"\s+", "");
                    if (n.Length > 0) pipelines.Add(n);
                }
            }
        }
        return (shaderTypes, vfs, pipelines);
    }

    private static bool IsPseudoInvocation(string name)
    {
        foreach (string tail in s_pseudoInvocationTails)
        {
            if (name.Contains(tail, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string? ExtractVariantClass(string text, Match openMatch, string macroName)
    {
        if (!ShaderTypeVariantSlot.TryGetValue(macroName, out int slot)) return null;
        int p = openMatch.Index + openMatch.Length;
        int depth = 1;
        List<string> args = new();
        var current = new System.Text.StringBuilder();
        while (p < text.Length && depth > 0)
        {
            char c = text[p];
            if (c == '(' || c == '<' || c == '[') { depth++; current.Append(c); }
            else if (c == ')') { depth--; if (depth == 0) { args.Add(current.ToString()); break; } current.Append(c); }
            else if (c == '>' || c == ']') { depth--; current.Append(c); }
            else if (c == ',' && depth == 1) { args.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
            p++;
        }
        if (slot >= args.Count) return null;
        return args[slot].Trim();
    }
}
