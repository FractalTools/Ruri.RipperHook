using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

public sealed record ShaderTypeClass(string CppName, IReadOnlyList<LayoutField> Fields, string SourceFile);

public sealed record LayoutField(string Kind, string CppType, string Name);

public static class ShaderTypeSeedScanner
{
    private static readonly Regex s_classDeclPattern = new(
        @"\bclass\s+(?:[A-Z][A-Z0-9_]+_API\s+)?(?<name>[A-Z][A-Za-z0-9_]+)"
        + @"\s*(?::|<[^>{}]+>\s*:)\s*public\s+"
        + @"(?:F[A-Z][A-Za-z0-9_]*Shader"
        + @"|TGlobalShader<[^>]+>"
        + @"|TShader<[^>]+>"
        + @"|TGlobalShaderPermutation<[^>]+>)\b",
        RegexOptions.Compiled);

    private static readonly Regex s_layoutFieldPattern = new(
        @"\bLAYOUT_FIELD\s*\(\s*(?<type>[A-Za-z_][A-Za-z_0-9<>:,\s]*?)\s*,\s*(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*[,\)]",
        RegexOptions.Compiled);

    public static IEnumerable<ShaderTypeClass> ScanAll(IEnumerable<string> sourceFiles)
    {
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (!text.Contains("LAYOUT_FIELD", StringComparison.Ordinal)) continue;
            if (!text.Contains(" : public F", StringComparison.Ordinal)
                && !text.Contains(": public T", StringComparison.Ordinal))
            {
                continue;
            }
            string stripped = UeSourceScanner.StripComments(text);

            foreach (Match classMatch in s_classDeclPattern.Matches(stripped))
            {
                string className = classMatch.Groups["name"].Value;
                int bodyStart = stripped.IndexOf('{', classMatch.Index + classMatch.Length);
                if (bodyStart < 0) continue;
                int bodyEnd = FindMatchingBrace(stripped, bodyStart);
                if (bodyEnd < 0) continue;
                string body = stripped[bodyStart..bodyEnd];

                var fields = new List<LayoutField>();
                foreach (Match fm in s_layoutFieldPattern.Matches(body))
                {
                    string typ = Regex.Replace(fm.Groups["type"].Value, @"\s+", "");
                    string name = fm.Groups["name"].Value;
                    string kind = typ switch
                    {
                        "FShaderParameter" => "Parameter",
                        "FShaderResourceParameter" => "Resource",
                        _ => typ,
                    };
                    fields.Add(new LayoutField(kind, typ, name));
                }
                if (fields.Count > 0)
                {
                    yield return new ShaderTypeClass(className, fields, file);
                }
            }
        }
    }

    private static int FindMatchingBrace(string s, int openPos)
    {
        int depth = 1;
        int i = openPos + 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i; }
            i++;
        }
        return -1;
    }
}
