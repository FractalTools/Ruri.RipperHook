using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

// Scan UE source for `class FFoo : public FShader|FGlobalShader|...` declarations,
// then within each class body collect `LAYOUT_FIELD(FShaderParameter, Name)` and
// `LAYOUT_FIELD(FShaderResourceParameter, Name)` declarations. Mirrors the
// Python generator's `emit_shader_type_seeds` pass — produces one JSON per
// LAYOUT_FIELD-using class with the parameter NAMES (source-declaration order)
// + placeholder offsets. Decompile-side `Pass180.TryReconcileGlobalsCB`
// pairs the seed names with cook-side `LooseParameterBuffers[0].Parameters[]`
// real offsets.
//
// Class-base must end at `Shader\b` (Stage 26 fix) — `FRHIShaderResourceView` /
// `FD3D11BoundShaderState` and friends contain `Shader` mid-token but aren't
// FShader subclasses; their LAYOUT_FIELDs are unrelated.
public sealed record ShaderTypeClass(string CppName, IReadOnlyList<LayoutField> Fields, string SourceFile);

public sealed record LayoutField(string Kind, string CppType, string Name);

public static class ShaderTypeSeedScanner
{
    // `class <ClassName>[ : public <Base>]` opener. Matches both
    // `class FFoo : public FGlobalShader` and templated
    // `class TBar<X> : public TGlobalShader<TBar<X>>` styles. Stage 26
    // anchored the base on `Shader\b` so RHI types don't slip in.
    private static readonly Regex s_classDeclPattern = new(
        @"\bclass\s+(?:[A-Z][A-Z0-9_]+_API\s+)?(?<name>[A-Z][A-Za-z0-9_]+)"
        + @"\s*(?::|<[^>{}]+>\s*:)\s*public\s+"
        + @"(?:F[A-Z][A-Za-z0-9_]*Shader"
        + @"|TGlobalShader<[^>]+>"
        + @"|TShader<[^>]+>"
        + @"|TGlobalShaderPermutation<[^>]+>)\b",
        RegexOptions.Compiled);

    // LAYOUT_FIELD(Type, Name) within a class body.
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
