using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

public readonly record struct ImplementMapping(string CppName, string ShaderBindingName, int BindingFlags, bool HasStaticSlot, string SourceFile);

public static class ImplementStructScanner
{
    private static readonly Regex s_pattern = new(
        @"\b(?<macro>IMPLEMENT_(?:UNIFORM_BUFFER_STRUCT|GLOBAL_SHADER_PARAMETER_STRUCT|GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT|STATIC_UNIFORM_BUFFER_STRUCT(?:_EX2|_EX)?|STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(?:_EX)?))\s*\(\s*"
        + @"(?<cpp>[A-Za-z_][A-Za-z_0-9]*)\s*,\s*"
        + @"""(?<binding>[^""]*)""",
        RegexOptions.Compiled);

    public static readonly IReadOnlyDictionary<string, (int Flags, bool HasStaticSlot)> MacroToFlags = new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
    {
        ["IMPLEMENT_UNIFORM_BUFFER_STRUCT"]                        = (1, false),        ["IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT"]               = (1, false),        ["IMPLEMENT_GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT"]         = (1, false),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT"]                 = (2, true),        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX"]              = (2, true),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX2"]             = (2, true),
        ["IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT"]      = (3, true),        ["IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT_EX"]   = (3, true),
    };

    public static Dictionary<string, ImplementMapping> ScanAll(IEnumerable<string> sourceFiles)
    {
        Dictionary<string, ImplementMapping> result = new(StringComparer.Ordinal);
        foreach (string file in sourceFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (text.Length == 0) continue;
            if (!text.Contains("IMPLEMENT_", StringComparison.Ordinal)) continue;
            string stripped = UeSourceScanner.StripComments(text);
            foreach (Match m in s_pattern.Matches(stripped))
            {
                string macro = m.Groups["macro"].Value;
                string cpp = m.Groups["cpp"].Value;
                string binding = m.Groups["binding"].Value;
                if (!MacroToFlags.TryGetValue(macro, out var info)) continue;
                result.TryAdd(cpp, new ImplementMapping(cpp, binding, info.Flags, info.HasStaticSlot, file));
            }
        }
        return result;
    }
}
