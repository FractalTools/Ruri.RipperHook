using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

public static class ImplementMacroExpander
{
    public sealed record MacroDef(string Name, IReadOnlyList<string> Params, string Body);

    private static readonly Regex s_defineImplPattern = new(
        @"#define\s+(?<name>IMPLEMENT_[A-Z0-9_]*?)\s*\((?<params>[^)]+)\)\s*\\?\s*\n(?<body>(?:[^\n]*\\\s*\n)*[^\n]*)",
        RegexOptions.Compiled);

    private static readonly Regex s_implShaderTypePattern = new(
        @"\bIMPLEMENT_(?:[A-Z][A-Z0-9_]*_)?SHADER_TYPE\s*\("
        + @"[^,]*,\s*"
        + @"([A-Za-z_][A-Za-z_0-9<>:,\s##]*?)\s*,",
        RegexOptions.Compiled);

    public static Dictionary<string, MacroDef> CollectMacroDefs(IEnumerable<string> sourceFiles)
    {
        Dictionary<string, MacroDef> raw = new(StringComparer.Ordinal);
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (!text.Contains("IMPLEMENT_", StringComparison.Ordinal)) continue;
            foreach (Match m in s_defineImplPattern.Matches(text))
            {
                string name = m.Groups["name"].Value;
                string body = m.Groups["body"].Value.Replace("\\\n", " ").Replace("\\\r\n", " ");
                string[] paramList = m.Groups["params"].Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                raw.TryAdd(name, new MacroDef(name, paramList, body));
            }
        }

        HashSet<string> qualified = new(StringComparer.Ordinal);
        foreach (var (name, def) in raw)
        {
            if (def.Body.Contains("##", StringComparison.Ordinal)) qualified.Add(name);
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, def) in raw)
            {
                if (qualified.Contains(name)) continue;
                foreach (string q in qualified)
                {
                    if (Regex.IsMatch(def.Body, @"\b" + Regex.Escape(q) + @"\s*\("))
                    {
                        qualified.Add(name);
                        changed = true;
                        break;
                    }
                }
            }
        }

        Dictionary<string, MacroDef> result = new(StringComparer.Ordinal);
        foreach (string name in qualified)
        {
            if (raw.TryGetValue(name, out MacroDef? def)) result.Add(name, def);
        }
        return result;
    }

    public static HashSet<string> ExpandInvocations(IReadOnlyDictionary<string, MacroDef> macroDefs, IEnumerable<string> sourceFiles)
    {
        HashSet<string> expansions = new(StringComparer.Ordinal);
        if (macroDefs.Count == 0) return expansions;

        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            bool anyMacroPresent = false;
            foreach (string macroName in macroDefs.Keys)
            {
                if (text.Contains(macroName, StringComparison.Ordinal)) { anyMacroPresent = true; break; }
            }
            if (!anyMacroPresent) continue;

            string expanded = text;
            for (int pass = 0; pass < 5; pass++)
            {
                (string next, bool hadChange) = ExpandOneLevel(expanded, macroDefs);
                if (!hadChange) break;
                expanded = next;
            }

            foreach (Match m in s_implShaderTypePattern.Matches(expanded))
            {
                string n = m.Groups[1].Value.Trim();
                if (n.Contains("##", StringComparison.Ordinal) || string.IsNullOrEmpty(n)) continue;
                n = Regex.Replace(n, @"\s+", "");
                expansions.Add(n);
            }
        }
        return expansions;
    }

    private static (string, bool) ExpandOneLevel(string text, IReadOnlyDictionary<string, MacroDef> macroDefs)
    {
        bool changed = false;
        string current = text;
        foreach (MacroDef def in macroDefs.Values)
        {
            Regex pattern = new(@"(?<![A-Za-z0-9_])" + Regex.Escape(def.Name) + @"\s*\(", RegexOptions.Compiled);
            var newText = new StringBuilder();
            int lastEnd = 0;
            foreach (Match m in pattern.Matches(current))
            {
                int argsStart = m.Index + m.Length;
                int closeIdx = FindMatchingCloseParen(current, argsStart);
                if (closeIdx < 0) continue;
                string argsRaw = current[argsStart..closeIdx];
                List<string> args = SplitTopLevel(argsRaw);
                if (args.Count != def.Params.Count) continue;

                string substituted = SubstituteArgs(def.Body, def.Params, args);
                newText.Append(current, lastEnd, m.Index - lastEnd);
                newText.Append(substituted);
                int afterClose = closeIdx + 1;
                if (afterClose < current.Length && current[afterClose] == ';') afterClose++;
                lastEnd = afterClose;
                changed = true;
            }
            if (changed)
            {
                newText.Append(current, lastEnd, current.Length - lastEnd);
                current = newText.ToString();
            }
        }
        return (current, changed);
    }

    private static int FindMatchingCloseParen(string s, int start)
    {
        int depth = 1;
        int i = start;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
            i++;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string s)
    {
        List<string> result = new();
        int depth = 0;
        var current = new StringBuilder();
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

    private static string SubstituteArgs(string body, IReadOnlyList<string> paramNames, IReadOnlyList<string> args)
    {
        string current = body;
        for (int i = 0; i < paramNames.Count; i++)
        {
            string p = paramNames[i];
            string a = args[i].Trim();
            current = Regex.Replace(current, @"##\s*" + Regex.Escape(p) + @"\b", _ => a);
            current = Regex.Replace(current, @"\b" + Regex.Escape(p) + @"\s*##", _ => a);
            current = Regex.Replace(current, @"\b" + Regex.Escape(p) + @"\b", _ => a);
        }
        return current;
    }
}
