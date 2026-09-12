using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

public static class ConstantsCollector
{
    private static readonly Regex s_definePattern = new(
        @"#define\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s+(?<value>0x[0-9A-Fa-f]+|\d+)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex s_constexprPattern = new(
        @"(?:static\s+)?constexpr\s+(?:int|uint|uint32|int32|size_t)\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*=\s*(?<value>0x[0-9A-Fa-f]+|\d+)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex s_enumMemberPattern = new(
        @"(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*=\s*(?<value>0x[0-9A-Fa-f]+|\d+)\s*[,}]",
        RegexOptions.Compiled);

    public static Dictionary<string, long> Collect(IEnumerable<string> sourceFiles)
    {
        Dictionary<string, long> constants = new(StringComparer.Ordinal);
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Length == 0) continue;
            if (!text.Contains("#define ", StringComparison.Ordinal)
                && !text.Contains("constexpr", StringComparison.Ordinal)
                && !text.Contains("enum ", StringComparison.Ordinal))
            {
                continue;
            }
            string stripped = UeSourceScanner.StripComments(text);

            foreach (Match m in s_definePattern.Matches(stripped))
            {
                string name = m.Groups["name"].Value;
                if (TryParseNumber(m.Groups["value"].Value, out long v))
                {
                    constants[name] = v;
                }
            }
            foreach (Match m in s_constexprPattern.Matches(stripped))
            {
                string name = m.Groups["name"].Value;
                if (TryParseNumber(m.Groups["value"].Value, out long v))
                {
                    constants[name] = v;
                }
            }
            foreach (Match m in s_enumMemberPattern.Matches(stripped))
            {
                string name = m.Groups["name"].Value;
                if (TryParseNumber(m.Groups["value"].Value, out long v))
                {
                    constants.TryAdd(name, v);
                }
            }
        }
        return constants;
    }

    private static bool TryParseNumber(string raw, out long value)
    {
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        return long.TryParse(raw, out value);
    }
}
