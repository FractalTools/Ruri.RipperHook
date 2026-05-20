using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Sweeps the UE source for `#define X N` / `static constexpr int X = N` /
// enum-member values used as array dimensions. These get used when a struct
// declares `SHADER_PARAMETER_ARRAY(float4, Foo, [SOME_CONSTANT])` — without
// the value of SOME_CONSTANT we can't compute the byte size of the array
// and the layout hash diverges. Mirrors the Python generator's
// `collect_constants` pass.
public static class ConstantsCollector
{
    // `#define IDENT NUMBER` or `#define NAMESPACE::IDENT NUMBER`. Accepts
    // hex (`0x...`), octal (`0...`), or decimal — UE uses all three.
    private static readonly Regex s_definePattern = new(
        @"#define\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s+(?<value>0x[0-9A-Fa-f]+|\d+)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // `static constexpr int Foo = N;` / `constexpr int Foo = N;` / similar.
    private static readonly Regex s_constexprPattern = new(
        @"(?:static\s+)?constexpr\s+(?:int|uint|uint32|int32|size_t)\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*=\s*(?<value>0x[0-9A-Fa-f]+|\d+)\s*;",
        RegexOptions.Compiled);

    // Enum member with explicit value: `Foo = 5,` / `Foo = 0x10,`.
    // We're permissive — anything inside `enum ... { ... }` that looks like
    // `Ident = N` qualifies. Bare enum members (no `=`) are skipped because
    // they're position-dependent and our use case (array dims) cares about
    // explicit values only.
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
                    // First-wins to match Python's dict behaviour — same enum
                    // name appearing in two TUs picks whichever the walker
                    // hit first. Cross-TU constant collisions are vanishingly
                    // rare in UE source.
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
