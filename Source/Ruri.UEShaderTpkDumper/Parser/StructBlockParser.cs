using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Extracts every `BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT[_WITH_CONSTRUCTOR]` /
// `BEGIN_UNIFORM_BUFFER_STRUCT[_WITH_CONSTRUCTOR]` / `BEGIN_SHADER_PARAMETER_STRUCT`
// block from UE source. Mirrors the Python generator's struct enumeration
// pass — feeds the layout walker downstream.
//
// The block's `Begin` line carries the struct's C++ name + (for UBs) its
// shader-side binding name (the `"View"` / `"Material"` literal). The
// `End_*` line just delimits the body. The body itself is whatever sits
// between, with `\`-line-continuations collapsed inside member macros.

public readonly record struct StructBlock(
    string Kind,        // "ub" (UNIFORM_BUFFER) | "global" (GLOBAL_SHADER_PARAMETER_STRUCT) | "param" (SHADER_PARAMETER_STRUCT)
    string CppName,     // the C++ struct identifier (`FViewUniformShaderParameters`)
    string BindingName, // shader-side cbuffer binding name; defaults to CppName when not provided
    string Body,        // raw text between BEGIN and END (line-continuations preserved)
    string SourceFile);

public static class StructBlockParser
{
    // Matches three families of opening macros:
    //
    //   BEGIN_UNIFORM_BUFFER_STRUCT(StructTypeName, PrefixKeywords)
    //   BEGIN_UNIFORM_BUFFER_STRUCT_WITH_CONSTRUCTOR(StructTypeName, PrefixKeywords)
    //   BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT(StructTypeName, PrefixKeywords)
    //   BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT_WITH_CONSTRUCTOR(StructTypeName, PrefixKeywords)
    //   BEGIN_SHADER_PARAMETER_STRUCT(StructTypeName, PrefixKeywords)
    //
    // PrefixKeywords is the dll-export macro (`ENGINE_API`, `RENDERCORE_API`,
    // `MyModule_API`, …) or empty. It is NOT a shader binding name — the
    // shader-side name (`"View"` / `"Material"`) comes from a separate
    // IMPLEMENT_*_STRUCT macro in a .cpp file. We capture but ignore it here;
    // a downstream IMPLEMENT scanner resolves the binding name.
    //
    // Capture groups:
    //   kind = UNIFORM_BUFFER_STRUCT | GLOBAL_SHADER_PARAMETER_STRUCT | SHADER_PARAMETER_STRUCT
    //   cpp = the C++ struct identifier (first arg)
    //   prefixKeywords = the dll-export prefix (second arg, ignored for naming)
    private static readonly Regex s_beginPattern = new(
        @"\bBEGIN_(?<kind>GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT|SHADER_PARAMETER_STRUCT)(?<suffix>_WITH_CONSTRUCTOR)?\s*\(\s*"
        + @"(?<cpp>[A-Za-z_][A-Za-z_0-9]*)\s*"
        + @"(?:,\s*(?<prefixKeywords>[^)]*?)\s*)?\)",
        RegexOptions.Compiled);

    // Match either END_GLOBAL_*, END_UNIFORM_*, or END_SHADER_PARAMETER_STRUCT.
    // We don't strictly require the BEGIN-kind to match END-kind because UE
    // source is consistent and we never see mixed pairs — but cross-pair
    // detection would be a sensible future safety net.
    private static readonly Regex s_endPattern = new(
        @"\bEND_(GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT|SHADER_PARAMETER_STRUCT)\s*\(\s*\)",
        RegexOptions.Compiled);

    public static IEnumerable<StructBlock> ParseFile(string filePath)
    {
        string text;
        try { text = File.ReadAllText(filePath); }
        catch { yield break; }

        // Quick reject — most files never declare these blocks.
        if (!text.Contains("BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT", StringComparison.Ordinal)
            && !text.Contains("BEGIN_UNIFORM_BUFFER_STRUCT", StringComparison.Ordinal)
            && !text.Contains("BEGIN_SHADER_PARAMETER_STRUCT", StringComparison.Ordinal))
        {
            yield break;
        }

        // Strip comments before matching so `// BEGIN_UNIFORM_BUFFER_STRUCT(...)`
        // inside a doc block doesn't get picked up.
        string source = UeSourceScanner.StripComments(text);

        int cursor = 0;
        while (cursor < source.Length)
        {
            Match begin = s_beginPattern.Match(source, cursor);
            if (!begin.Success) break;

            int blockBodyStart = begin.Index + begin.Length;
            Match end = s_endPattern.Match(source, blockBodyStart);
            if (!end.Success) break;

            string kindRaw = begin.Groups["kind"].Value;
            string kind = kindRaw switch
            {
                "UNIFORM_BUFFER_STRUCT" => "ub",
                "GLOBAL_SHADER_PARAMETER_STRUCT" => "global",
                _ => "param",
            };

            string cppName = begin.Groups["cpp"].Value;
            // Default the binding name to the C++ struct name. The real
            // shader-side binding name (`"View"`, `"Material"`, etc.) lives
            // on the matching IMPLEMENT_*_STRUCT macro in some .cpp; an
            // ImplementStructScanner pass overrides this later.
            string bindingName = cppName;

            string body = source[blockBodyStart..end.Index];
            yield return new StructBlock(kind, cppName, bindingName, body, filePath);
            cursor = end.Index + end.Length;
        }
    }
}
