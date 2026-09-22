using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

/// <summary>
/// How one uniform buffer struct is implemented: the shader-visible name it binds under, how it
/// binds, whether it owns a static slot, and the usage flags the implementing macro states.
/// </summary>
public readonly record struct ImplementMapping(string CppName, string ShaderBindingName, int BindingFlags, bool HasStaticSlot, string UsageFlags, string SourceFile);

/// <summary>
/// The IMPLEMENT_* line of every uniform buffer struct, which is where the engine states the
/// facts the layout hash folds in beyond the members themselves. Each macro spells out its
/// binding model; the _EX variants carry a trailing usage-flags argument on 5.x, and those flags
/// change the hash from 5.5 on, so they are kept as the names the source wrote and valued by
/// the engine's own enum later.
/// </summary>
public static class ImplementStructScanner
{
    private static readonly Regex Pattern = new(
        @"\b(?<macro>IMPLEMENT_(?:UNIFORM_BUFFER_STRUCT(?:_EX)?|GLOBAL_SHADER_PARAMETER_STRUCT|GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT|STATIC_UNIFORM_BUFFER_STRUCT(?:_EX2|_EX)?|STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(?:_EX)?))\s*\(",
        RegexOptions.Compiled);

    /// <summary>Binding flags and static-slot ownership per macro, as the engine's own definitions of them state.</summary>
    public static readonly IReadOnlyDictionary<string, (int Flags, bool HasStaticSlot)> MacroToFlags = new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
    {
        ["IMPLEMENT_UNIFORM_BUFFER_STRUCT"] = (1, false),
        ["IMPLEMENT_UNIFORM_BUFFER_STRUCT_EX"] = (1, false),
        ["IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT"] = (1, false),
        ["IMPLEMENT_GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT"] = (1, false),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT"] = (2, true),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX"] = (2, true),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX2"] = (2, true),
        ["IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT"] = (3, true),
        ["IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT_EX"] = (3, true),
    };

    public static Dictionary<string, ImplementMapping> ScanAll(IEnumerable<string> sourceFiles)
    {
        Dictionary<string, ImplementMapping> result = new(StringComparer.Ordinal);
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Length == 0 || !text.Contains("IMPLEMENT_", StringComparison.Ordinal))
            {
                continue;
            }
            string stripped = UeSourceScanner.StripComments(text);
            foreach (Match match in Pattern.Matches(stripped))
            {
                string macro = match.Groups["macro"].Value;
                if (!MacroToFlags.TryGetValue(macro, out (int Flags, bool HasStaticSlot) info))
                {
                    continue;
                }
                int close = MatchingParen(stripped, match.Index + match.Length);
                if (close < 0)
                {
                    continue;
                }
                List<string> arguments = Arguments(stripped[(match.Index + match.Length)..close]);
                if (arguments.Count < 2)
                {
                    continue;
                }
                string cpp = arguments[0];
                string binding = arguments[1].Trim('"');
                string usage = UsageArgument(macro, arguments);
                result.TryAdd(cpp, new ImplementMapping(cpp, binding, info.Flags, info.HasStaticSlot, usage, file));
            }
        }
        return result;
    }

    /// <summary>
    /// The usage-flags argument, where the macro takes one: last for the _EX and _EX2 forms
    /// (after the binding flags on _EX2 of the static kind), none otherwise.
    /// </summary>
    private static string UsageArgument(string macro, IReadOnlyList<string> arguments)
    {
        bool takesUsage = macro.EndsWith("_EX", StringComparison.Ordinal) || macro.EndsWith("_EX2", StringComparison.Ordinal);
        if (!takesUsage)
        {
            return string.Empty;
        }
        string last = arguments[^1];
        return last.Contains("EUsageFlags", StringComparison.Ordinal) ? last : string.Empty;
    }

    private static int MatchingParen(string text, int start)
    {
        int depth = 1;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<string> Arguments(string inner)
    {
        List<string> arguments = new();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '(' || c == '<') depth++;
            else if (c == ')' || c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                arguments.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }
        arguments.Add(inner[start..].Trim());
        return arguments;
    }
}
