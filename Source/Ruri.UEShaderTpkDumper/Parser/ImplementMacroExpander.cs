using System.Text;
using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Recursively expand wrapper macros that produce IMPLEMENT_*_SHADER_TYPE
// via `##`-concatenation. The canonical UE pattern is:
//
//   #define IMPLEMENT_LIGHTMAPPED_DENSITY_SHADER_TYPE(LightMapPolicy, LightMapPolicyName) \
//       typedef TLightMapDensityPS<LightMapPolicy> TLightMapDensityPS##LightMapPolicyName; \
//       IMPLEMENT_MATERIAL_SHADER_TYPE(template<>, TLightMapDensityPS##LightMapPolicyName, ...);
//
// Then `IMPLEMENT_LIGHTMAPPED_DENSITY_SHADER_TYPE(FDummyLightMapPolicy, FDummyLightMapPolicy)`
// expands (after `##` substitution) to a literal
// `IMPLEMENT_MATERIAL_SHADER_TYPE(template<>, TLightMapDensityPSFDummyLightMapPolicy, ...)`
// — and THAT'S the class name we need to hash for the runtime to resolve.
//
// Mirrors the Python generator's `_collect_implement_macro_definitions` +
// `_expand_invocations`. Without this, every templated `TBasePassPS<...>`
// specialisation, every `TLightMapDensityPS<...>`, every `TShadowDepthPS<...>`,
// etc., is missing from the hash-to-name index.
public static class ImplementMacroExpander
{
    public sealed record MacroDef(string Name, IReadOnlyList<string> Params, string Body);

    // `#define IMPLEMENT_<X>(params) body` where `body` (after collapsing
    // `\` line-continuations) contains `##` OR invokes a wrapper that
    // transitively contains `##`. Mirrors Python's `_MACRO_DEF_WITH_HASHHASH_RE`.
    private static readonly Regex s_defineImplPattern = new(
        @"#define\s+(?<name>IMPLEMENT_[A-Z0-9_]*?)\s*\((?<params>[^)]+)\)\s*\\?\s*\n(?<body>(?:[^\n]*\\\s*\n)*[^\n]*)",
        RegexOptions.Compiled);

    // After expansion: pull the IMPLEMENT_*_SHADER_TYPE class name out of the
    // expanded text. Same shape as IndexNameCollector's main regex.
    private static readonly Regex s_implShaderTypePattern = new(
        @"\bIMPLEMENT_(?:[A-Z][A-Z0-9_]*_)?SHADER_TYPE\s*\("
        + @"[^,]*,\s*"
        + @"([A-Za-z_][A-Za-z_0-9<>:,\s##]*?)\s*,",
        RegexOptions.Compiled);

    // Collect every `IMPLEMENT_<X>` macro definition that ends up producing
    // an IMPLEMENT_*_SHADER_TYPE — either directly (body has ##) or
    // transitively (body invokes another qualified macro).
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

        // Keep only macros that eventually produce a `##`-concatenated
        // IMPLEMENT_*_SHADER_TYPE invocation. A macro qualifies if its body
        // either contains `##` directly OR invokes another qualified macro.
        // Iterate the closure until stable.
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

    // For every invocation of a qualified macro, recursively expand args
    // into the body, then pull the final IMPLEMENT_*_SHADER_TYPE class name.
    // Returns the SET of fully-expanded specialised class names. Caller adds
    // them to the hash-to-name index.
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

            // Expand iteratively. After each pass new invocations may surface
            // because outer macros invoked inner ones. Cap at 5 passes.
            string expanded = text;
            for (int pass = 0; pass < 5; pass++)
            {
                (string next, bool hadChange) = ExpandOneLevel(expanded, macroDefs);
                if (!hadChange) break;
                expanded = next;
            }

            // Pull every IMPLEMENT_*_SHADER_TYPE seen in the expanded text.
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

    // Expand every invocation of every qualified macro in `text` once.
    // Returns (expanded_text, any_substitution_happened). Caller iterates
    // until no more changes.
    private static (string, bool) ExpandOneLevel(string text, IReadOnlyDictionary<string, MacroDef> macroDefs)
    {
        bool changed = false;
        string current = text;
        foreach (MacroDef def in macroDefs.Values)
        {
            // `(?<![A-Za-z0-9_])` is the non-`_`-aware word boundary —
            // matches the macro name in identifier context like `\bMACRO\(`.
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
                // Skip trailing `;` if present (line-terminator on the invocation).
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
        // Three substitution rules per param (preserving Python generator's
        // semantics):
        //   1. `##<param>` -> arg value (consumes the `##` separator)
        //   2. `<param>##` -> arg value (same)
        //   3. `<param>` -> arg value (plain identifier reference)
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
