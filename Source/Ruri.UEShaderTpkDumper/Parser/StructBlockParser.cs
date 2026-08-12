using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;


public readonly record struct StructBlock(
    string Kind,    string CppName,    string BindingName,    string Body,    string SourceFile);

public static class StructBlockParser
{
    private static readonly Regex s_beginPattern = new(
        @"\bBEGIN_(?<kind>GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT|SHADER_PARAMETER_STRUCT)(?<suffix>_WITH_CONSTRUCTOR)?\s*\(\s*"
        + @"(?<cpp>[A-Za-z_][A-Za-z_0-9]*)\s*"
        + @"(?:,\s*(?<prefixKeywords>[^)]*?)\s*)?\)",
        RegexOptions.Compiled);

    private static readonly Regex s_endPattern = new(
        @"\bEND_(GLOBAL_SHADER_PARAMETER_STRUCT|UNIFORM_BUFFER_STRUCT|SHADER_PARAMETER_STRUCT)\s*\(\s*\)",
        RegexOptions.Compiled);

    public static IEnumerable<StructBlock> ParseFile(string filePath)
    {
        string text;
        try { text = File.ReadAllText(filePath); }
        catch { yield break; }

        if (!text.Contains("BEGIN_GLOBAL_SHADER_PARAMETER_STRUCT", StringComparison.Ordinal)
            && !text.Contains("BEGIN_UNIFORM_BUFFER_STRUCT", StringComparison.Ordinal)
            && !text.Contains("BEGIN_SHADER_PARAMETER_STRUCT", StringComparison.Ordinal))
        {
            yield break;
        }

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
            string bindingName = cppName;

            string body = source[blockBodyStart..end.Index];
            yield return new StructBlock(kind, cppName, bindingName, body, filePath);
            cursor = end.Index + end.Length;
        }
    }
}
