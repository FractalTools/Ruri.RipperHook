using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Scans `.cpp` files for `IMPLEMENT_*_STRUCT(StructTypeName, "ShaderBindingName"[, StaticSlot])`
// to (1) recover the shader-side binding NAME the cooked HLSL uses for each
// uniform buffer (e.g. `"View"`, `"Material"`, `"LandscapeParameters"`) and
// (2) record the binding-flags + static-slot bits that XOR into the layout
// hash.
//
// Mirrors the Python generator's `_IMPLEMENT_RE` scan. The exhaustive macro
// list from `ShaderParameterMacros.h` is:
//
//   IMPLEMENT_UNIFORM_BUFFER_STRUCT(StructType, ShaderName)
//   IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT(StructType, ShaderName)
//   IMPLEMENT_GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT(StructType, ShaderName)
//   IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT(StructType, ShaderName, StaticSlot)
//   IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX(StructType, ShaderName, StaticSlot, BindingFlags)
//   IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX2(StructType, ShaderName, StaticSlot, BindingFlags, UsageFlags)
//   IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(StructType, ShaderName, StaticSlot)
//   IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT_EX(StructType, ShaderName, StaticSlot, UsageFlags)
//
// Each macro picks a specific `EUniformBufferBindingFlags` value that XORs
// into the layout hash. We map them to the same flag bit pattern the engine
// uses.
public readonly record struct ImplementMapping(string CppName, string ShaderBindingName, int BindingFlags, bool HasStaticSlot, string SourceFile);

public static class ImplementStructScanner
{
    // Matches every IMPLEMENT_*_STRUCT family macro. Captures the macro name
    // so we can map to the right BindingFlags / StaticSlot combination.
    private static readonly Regex s_pattern = new(
        @"\b(?<macro>IMPLEMENT_(?:UNIFORM_BUFFER_STRUCT|GLOBAL_SHADER_PARAMETER_STRUCT|GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT|STATIC_UNIFORM_BUFFER_STRUCT(?:_EX2|_EX)?|STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT(?:_EX)?))\s*\(\s*"
        + @"(?<cpp>[A-Za-z_][A-Za-z_0-9]*)\s*,\s*"
        + @"""(?<binding>[^""]*)""",
        RegexOptions.Compiled);

    // (Shader-bit | Static-bit). Per `EUniformBufferBindingFlags`:
    //   Shader = 1 << 0 = 1
    //   Static = 1 << 1 = 2
    //   StaticAndShader = Shader | Static = 3
    public static readonly IReadOnlyDictionary<string, (int Flags, bool HasStaticSlot)> MacroToFlags = new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
    {
        ["IMPLEMENT_UNIFORM_BUFFER_STRUCT"]                        = (1, false), // Shader
        ["IMPLEMENT_GLOBAL_SHADER_PARAMETER_STRUCT"]               = (1, false), // Shader (same family)
        ["IMPLEMENT_GLOBAL_SHADER_PARAMETER_ALIAS_STRUCT"]         = (1, false),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT"]                 = (2, true),  // Static
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX"]              = (2, true),
        ["IMPLEMENT_STATIC_UNIFORM_BUFFER_STRUCT_EX2"]             = (2, true),
        ["IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT"]      = (3, true),  // StaticAndShader
        ["IMPLEMENT_STATIC_AND_SHADER_UNIFORM_BUFFER_STRUCT_EX"]   = (3, true),
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
                // First-wins on duplicate StructType — unusual but defensive.
                result.TryAdd(cpp, new ImplementMapping(cpp, binding, info.Flags, info.HasStaticSlot, file));
            }
        }
        return result;
    }
}
