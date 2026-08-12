using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

public sealed record MemberLine(
    string Macro,    string CppType,    string Name,    string? ArrayDecl,    string? ShaderType,    string Ubmt){
    public bool IsResource => !string.IsNullOrEmpty(Ubmt)
        && Ubmt != "NESTED_STRUCT"
        && Ubmt != "INCLUDED_STRUCT";
}

public static class MemberLineParser
{
    public static IEnumerable<MemberLine> ParseBody(string body)
    {
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
            cppType = typeOrHlsl;        }
        else
        {
            cppType = typeOrHlsl;
        }

        return new MemberLine(macro, cppType, name, arrayDecl, shaderType, info.UbmtName);
    }
}
