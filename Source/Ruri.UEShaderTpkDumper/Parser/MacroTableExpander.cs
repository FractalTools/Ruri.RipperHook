using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

public static class MacroTableExpander
{
    public sealed record TableEntry(string Name, string[] Params, string Body);

    private static readonly Regex s_defineRe = new(
        @"^[ \t]*#define[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<params>\([^)]*\))?[ \t]+(?<body>[^\n]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex s_continuationRe = new(@"\\\r?\n", RegexOptions.Compiled);

    private static readonly string[] s_skipPrefixes =
    {
        "SHADER_PARAMETER", "BEGIN_", "END_", "INTERNAL_", "IMPLEMENT_",
        "RENDER_TARGET_", "RDG_",
    };

    public static Dictionary<string, TableEntry> Collect(IEnumerable<string> sourceFiles)
    {
        Dictionary<string, TableEntry> tables = new(StringComparer.Ordinal);
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Length == 0) continue;
            if (!text.Contains("SHADER_PARAMETER", StringComparison.Ordinal)
                && !text.Contains("_UNIFORM_BUFFER_MEMBER", StringComparison.Ordinal))
            {
                continue;
            }
            string joined = s_continuationRe.Replace(text, " ");
            foreach (Match m in s_defineRe.Matches(joined))
            {
                string name = m.Groups["name"].Value;
                string body = m.Groups["body"].Value.Trim();
                if (!body.Contains("SHADER_PARAMETER", StringComparison.Ordinal)
                    && !body.Contains("_UNIFORM_BUFFER_MEMBER", StringComparison.Ordinal))
                {
                    continue;
                }
                bool skip = false;
                foreach (string prefix in s_skipPrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) { skip = true; break; }
                }
                if (skip) continue;
                string[] paramList = ParseParams(m.Groups["params"].Value);
                tables.TryAdd(name, new TableEntry(name, paramList, body));
            }
        }
        return tables;
    }

    private static string[] ParseParams(string paramsRaw)
    {
        if (string.IsNullOrEmpty(paramsRaw)) return Array.Empty<string>();
        string inner = paramsRaw.Trim('(', ')').Trim();
        if (inner.Length == 0) return Array.Empty<string>();
        return inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static string Expand(string text, IReadOnlyDictionary<string, TableEntry> tables, int depth = 0)
    {
        if (depth > 6) return text;
        bool changed = false;
        string current = text;

        foreach (TableEntry entry in tables.Values)
        {
            if (entry.Params.Length == 0) continue;
            if (!current.Contains(entry.Name, StringComparison.Ordinal)) continue;
            Regex call = new(@"\b" + Regex.Escape(entry.Name) + @"\s*\(([^()]*)\)", RegexOptions.Compiled);
            string replaced = call.Replace(current, m =>
            {
                string args = m.Groups[1].Value;
                List<string> argList = SplitTopLevel(args);
                string expanded = entry.Body;
                for (int i = 0; i < entry.Params.Length && i < argList.Count; i++)
                {
                    string param = entry.Params[i];
                    string actual = argList[i].Trim();
                    expanded = Regex.Replace(expanded, @"\b" + Regex.Escape(param) + @"\b", actual);
                }
                return expanded;
            });
            if (!ReferenceEquals(replaced, current))
            {
                current = replaced;
                changed = true;
            }
        }

        foreach (TableEntry entry in tables.Values)
        {
            if (entry.Params.Length != 0) continue;
            if (!current.Contains(entry.Name, StringComparison.Ordinal)) continue;
            Regex token = new(@"\b" + Regex.Escape(entry.Name) + @"\b", RegexOptions.Compiled);
            string replaced = token.Replace(current, entry.Body);
            if (!ReferenceEquals(replaced, current))
            {
                current = replaced;
                changed = true;
            }
        }

        return changed ? Expand(current, tables, depth + 1) : current;
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
}
