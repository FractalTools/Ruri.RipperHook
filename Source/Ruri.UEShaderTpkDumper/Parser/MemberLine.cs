using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// A single macro call inside a `BEGIN_*` block, after `\`-continuation
// collapse. Mirrors the Python generator's `Member` dataclass.
public sealed record MemberLine(
    string Macro,          // SHADER_PARAMETER, SHADER_PARAMETER_TEXTURE, ...
    string CppType,        // FMatrix44f, FViewUniformShaderParameters, Texture2D, ...
    string Name,           // member name
    string? ArrayDecl,     // raw "[N]" / "[Foo::MaxItems]" if present
    string? ShaderType,    // SHADER_PARAMETER_TEXTURE's HLSL type hint (Texture2D, Sampler, ...)
    string Ubmt)           // resolved UBMT slot name (no "UBMT_" prefix)
{
    public bool IsResource => !string.IsNullOrEmpty(Ubmt)
        && Ubmt != "NESTED_STRUCT"
        && Ubmt != "INCLUDED_STRUCT";
}

public static class MemberLineParser
{
    // Walk the block body, finding every `MACRO(...)` call from our catalog.
    // Returns them in declaration order — critical because the layout walker
    // pads members based on declared sequence.
    public static IEnumerable<MemberLine> ParseBody(string body)
    {
        // Collapse `\` continuations so multi-line macros (rare but possible
        // for SHADER_PARAMETER_STRUCT_INCLUDE with long type names) parse as
        // a single token stream. Python does this implicitly because the
        // regex matches across lines; in C# we explicitly drop continuations.
        string collapsed = body.Replace("\\\r\n", " ").Replace("\\\n", " ");

        Regex opener = new(@"\b(" + MemberMacros.MacroNameRegex + @")\s*\(", RegexOptions.Compiled);
        Match m = opener.Match(collapsed);
        while (m.Success)
        {
            string macroName = m.Groups[1].Value;
            int afterOpenParen = m.Index + m.Length;
            int endParen = FindMatchingParen(collapsed, afterOpenParen);
            if (endParen < 0) yield break;
            string argsRaw = collapsed[afterOpenParen..endParen];
            List<string> args = SplitTopLevel(argsRaw).Select(static s => s.Trim()).ToList();

            MemberLine? line = BuildMember(macroName, args);
            if (line != null) yield return line;

            m = opener.Match(collapsed, endParen + 1);
        }
    }

    private static int FindMatchingParen(string s, int start)
    {
        int depth = 1;
        int i = start;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
            i++;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string s)
    {
        // Splits at top-level commas (skipping nested <>, (), [], {}). Matches
        // the Python generator's `split_top_commas`.
        List<string> result = new();
        int depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (c == ',' && depth == 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                if (c == '<' || c == '(' || c == '[' || c == '{') depth++;
                else if (c == '>' || c == ')' || c == ']' || c == '}') depth--;
                current.Append(c);
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static MemberLine? BuildMember(string macro, IReadOnlyList<string> args)
    {
        if (!MemberMacros.Catalog.TryGetValue(macro, out MemberMacroInfo info)) return null;

        // Member-macro shape per macro family — mirroring `gen_ub_metadata.py`'s
        // walker:
        //
        //   SHADER_PARAMETER(Type, Name)
        //   SHADER_PARAMETER_EX(Type, Name, Precision)
        //   SHADER_PARAMETER_ARRAY(Type, Name, [N])
        //   SHADER_PARAMETER_ARRAY_EX(Type, Name, [N], Precision)
        //   SHADER_PARAMETER_SCALAR_ARRAY(Type, Name, [N])
        //   SHADER_PARAMETER_TEXTURE(HlslType, Name)
        //   SHADER_PARAMETER_TEXTURE_ARRAY(HlslType, Name, [N])
        //   SHADER_PARAMETER_SAMPLER(HlslType, Name)
        //   ...
        //   SHADER_PARAMETER_STRUCT(StructType, Name)
        //   SHADER_PARAMETER_STRUCT_INCLUDE(StructType, Name)
        //   RENDER_TARGET_BINDING_SLOTS()  — no args (we still record it for layout slot tracking)
        //
        // The Type/Name pair is invariant — the array/precision suffix is
        // optional. We trust the catalog's IsResource flag rather than re-
        // detecting from the macro name here.
        if (string.Equals(macro, "RENDER_TARGET_BINDING_SLOTS", StringComparison.Ordinal))
        {
            return new MemberLine(macro, string.Empty, string.Empty, null, null, info.UbmtName);
        }

        if (args.Count < 2) return null;
        string typeOrHlsl = args[0];
        string name = args[1];
        string? arrayDecl = null;
        if (args.Count >= 3 && args[2].Length > 0 && args[2][0] == '[')
        {
            arrayDecl = args[2];
        }

        string cppType;
        string? shaderType = null;
        if (info.IsResource)
        {
            shaderType = typeOrHlsl;
            cppType = typeOrHlsl;  // resources don't have a numeric size — recorded for diagnostics
        }
        else
        {
            cppType = typeOrHlsl;
        }

        return new MemberLine(macro, cppType, name, arrayDecl, shaderType, info.UbmtName);
    }
}
