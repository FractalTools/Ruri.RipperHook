using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

/// <summary>How many of a member the engine emits, stated as what it counts rather than as C++.</summary>
public sealed record MemberCount(string Source, int Multiply, int DivideRoundUp, string Expression);

/// <summary>
/// One member of the material's uniform buffer: a literal name, or a name template the engine
/// numbers per element of whatever it repeats over.
/// </summary>
public sealed record MaterialBufferMember(
    string Name,
    string NameTemplate,
    string ShaderType,
    string Ubmt,
    int Rows,
    int Columns,
    MemberCount Count,
    string RepeatOver,
    string Condition);

/// <summary>The whole recipe, as one engine version writes it.</summary>
public sealed record MaterialBufferRecipe(
    string SourceFile,
    IReadOnlyList<string> TextureParameterTypes,
    IReadOnlyList<MaterialBufferMember> Members,
    int StructAlignment,
    int PointerAlignment,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// The material uniform buffer as the ENGINE builds it, read out of
/// <c>FUniformExpressionSet::CreateBufferStruct()</c>.
///
/// That function is the only statement of what a material's constant buffer contains and what
/// each member is called, and it changes with the engine: 4.26 packs numbers as
/// VectorExpressions + ScalarExpressions, 5.x as one PreshaderBuffer; 4.26 knows five kinds of
/// texture parameter, 5.5 seven, with names to match. Reading it here turns all of that into
/// data the decompiler replays with a material's own counts, so no version's member names or
/// bucket order is ever written into the reader.
/// </summary>
public static class MaterialUniformBufferScanner
{
    private const string RelativeSource = "Engine/Source/Runtime/Engine/Private/Materials/MaterialUniformExpressions.cpp";
    private const string RelativeEnum = "Engine/Source/Runtime/Engine/Public/MaterialShared.h";

    /// <summary>
    /// Where an engine states its shader-parameter alignments. 4.26 says it in RHI.h, 5.5 moved
    /// the same two macros to RHIDefinitions.h, so the header is searched for rather than named.
    /// </summary>
    private static readonly string[] RelativeAlignmentHeaders =
    {
        "Engine/Source/Runtime/RHI/Public/RHI.h",
        "Engine/Source/Runtime/RHI/Public/RHIDefinitions.h",
    };

    private static readonly Regex NameTemplate = new(
        @"(?<var>\w+)\s*\[\s*i\s*\]\s*=\s*FString::Printf\s*\(\s*TEXT\(""(?<tmpl>[^""]+)""\)",
        RegexOptions.Compiled);

    private static readonly Regex MemberCall = new(
        @"new\s*\(\s*Members\s*\)\s*FShaderParametersMetadata::FMember\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TextureLoop = new(
        @"UniformTextureParameters\s*\[\s*\(uint32\)\s*EMaterialTextureParameterType::(?<kind>\w+)\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex EnumBlock = new(
        @"enum\s+class\s+EMaterialTextureParameterType\s*:\s*\w+\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static MaterialBufferRecipe? Scan(string engineRoot)
    {
        string sourceFile = Path.Combine(engineRoot, RelativeSource.Replace('/', Path.DirectorySeparatorChar));
        string enumFile = Path.Combine(engineRoot, RelativeEnum.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourceFile))
        {
            return null;
        }

        string text = File.ReadAllText(sourceFile);
        string? body = FunctionBody(text, "FUniformExpressionSet::CreateBufferStruct");
        if (body is null)
        {
            return null;
        }

        Dictionary<string, string> templates = new(StringComparer.Ordinal);
        foreach (Match match in NameTemplate.Matches(body))
        {
            templates[match.Groups["var"].Value] = match.Groups["tmpl"].Value;
        }

        List<MaterialBufferMember> members = new();
        List<string> unresolved = new();
        foreach ((string call, string loop, string condition) in MemberCalls(body))
        {
            MaterialBufferMember? member = Member(call, loop, condition, templates, unresolved);
            if (member is not null)
            {
                members.Add(member);
            }
        }

        return new MaterialBufferRecipe(RelativeSource, TextureTypes(enumFile), members,
            Alignment(engineRoot, "SHADER_PARAMETER_STRUCT_ALIGNMENT", unresolved),
            Alignment(engineRoot, "SHADER_PARAMETER_POINTER_ALIGNMENT", unresolved),
            unresolved);
    }

    /// <summary>The body of one function, brace-matched from its opening brace.</summary>
    private static string? FunctionBody(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        int open = text.IndexOf('{', at);
        if (open < 0)
        {
            return null;
        }
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0)
            {
                return text[(open + 1)..i];
            }
        }
        return null;
    }

    /// <summary>
    /// Every member call in the body, each with the innermost loop or condition it sits under --
    /// which is what says how many times the engine emits it.
    /// </summary>
    private static IEnumerable<(string Call, string Loop, string Condition)> MemberCalls(string body)
    {
        List<(int Depth, string Header)> scopes = new();
        string pending = string.Empty;
        int depth = 0;
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            foreach (char c in line)
            {
                if (c == '{')
                {
                    depth++;
                    scopes.Add((depth, pending));
                    pending = string.Empty;
                }
                else if (c == '}')
                {
                    scopes.RemoveAll(scope => scope.Depth == depth);
                    depth--;
                }
            }
            if (line.StartsWith("for", StringComparison.Ordinal) || line.StartsWith("if", StringComparison.Ordinal))
            {
                pending = line;
                if (!line.Contains('{'))
                {
                    scopes.Add((depth + 1, line));
                }
            }
            if (!MemberCall.IsMatch(line))
            {
                continue;
            }
            // A member repeats over the nearest enclosing LOOP, whatever conditions sit between
            // the two: one of them emits a second page table only for a stack with more than four
            // layers, and reading that condition as the repeat said "emitted once per material".
            string loop = string.Empty;
            string condition = string.Empty;
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                string header = scopes[i].Header;
                if (header.Length == 0) continue;
                if (header.StartsWith("for", StringComparison.Ordinal))
                {
                    loop = header;
                    break;
                }
                if (condition.Length == 0) condition = header;
            }
            yield return (line, loop, condition);
        }
    }

    private static MaterialBufferMember? Member(string call, string loop, string condition,
        IReadOnlyDictionary<string, string> templates, List<string> unresolved)
    {
        int open = call.IndexOf('(', call.IndexOf("FMember", StringComparison.Ordinal));
        if (open < 0)
        {
            return null;
        }
        List<string> arguments = Arguments(call, open);
        if (arguments.Count < 6)
        {
            unresolved.Add(call);
            return null;
        }

        string name = string.Empty;
        string template = string.Empty;
        string first = arguments[0];
        Match literal = Regex.Match(first, @"TEXT\(""(?<text>[^""]*)""\)");
        if (literal.Success)
        {
            name = literal.Groups["text"].Value;
        }
        else
        {
            Match indexed = Regex.Match(first, @"\*\s*(?<var>\w+)\s*\[");
            if (indexed.Success && templates.TryGetValue(indexed.Groups["var"].Value, out string? found))
            {
                template = found;
            }
            else
            {
                unresolved.Add(call);
                return null;
            }
        }

        string shaderType = Regex.Match(arguments[1], @"TEXT\(""(?<text>[^""]*)""\)") is { Success: true } t
            ? t.Groups["text"].Value
            : string.Empty;
        string ubmt = arguments.FirstOrDefault(a => a.Contains("UBMT_", StringComparison.Ordinal))?.Trim() ?? string.Empty;

        // The last two arguments are always the element count and a null; the two before it are
        // the member's rows and columns.
        string countExpression = arguments.Count >= 2 ? arguments[^2].Trim() : "0";
        int rows = Number(arguments, arguments.Count - 4);
        int columns = Number(arguments, arguments.Count - 3);

        return new MaterialBufferMember(name, template, shaderType, ubmt, rows, columns,
            Count(countExpression, unresolved), Repeat(loop), Condition(condition));
    }

    private static int Number(IReadOnlyList<string> arguments, int index) =>
        index >= 0 && index < arguments.Count && int.TryParse(arguments[index].Trim(), out int value) ? value : 0;

    private static List<string> Arguments(string call, int open)
    {
        List<string> arguments = new();
        int depth = 0;
        int start = open + 1;
        for (int i = start; i < call.Length; i++)
        {
            char c = call[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0)
                {
                    arguments.Add(call[start..i]);
                    break;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(call[start..i]);
                start = i + 1;
            }
        }
        return arguments;
    }

    /// <summary>What a member repeats over, as the loop it sits in states it.</summary>
    private static string Repeat(string loop)
    {
        if (loop.Length == 0)
        {
            return "Once";
        }
        Match textures = TextureLoop.Match(loop);
        if (textures.Success)
        {
            return "Textures:" + textures.Groups["kind"].Value;
        }
        if (loop.Contains("UniformExternalTextureParameters", StringComparison.Ordinal)) return "ExternalTextures";
        if (loop.Contains("UniformTextureCollectionParameters", StringComparison.Ordinal)) return "TextureCollections";
        if (loop.Contains("VTStacks", StringComparison.Ordinal)) return "VirtualTextureStacks";
        return "Once";
    }

    /// <summary>
    /// What has to hold for one element to get this member. Only one exists in any engine version
    /// read so far -- a stack wide enough to need a second page table -- and it is stated as the
    /// threshold rather than as C++ so a reader can answer it from the stack's own layer count.
    /// </summary>
    private static string Condition(string condition)
    {
        if (condition.Length == 0)
        {
            return string.Empty;
        }
        Match layers = Regex.Match(condition, @"GetNumLayers\(\)\s*>\s*(?<threshold>\d+)");
        return layers.Success ? "VirtualTextureStackLayersAbove:" + layers.Groups["threshold"].Value : condition.Trim();
    }

    /// <summary>How many elements a member holds, as what it counts rather than as C++.</summary>
    private static MemberCount Count(string expression, List<string> unresolved)
    {
        string trimmed = expression.Trim();
        if (trimmed is "0" or "NULL")
        {
            return new MemberCount("None", 1, 1, trimmed);
        }
        Match divided = Regex.Match(trimmed, @"^\(\s*(?<inner>.+?)\s*\+\s*(?<bias>\d+)\s*\)\s*/\s*(?<divisor>\d+)$");
        if (divided.Success)
        {
            MemberCount inner = Count(divided.Groups["inner"].Value, unresolved);
            return inner with { DivideRoundUp = int.Parse(divided.Groups["divisor"].Value), Expression = trimmed };
        }
        Match multiplied = Regex.Match(trimmed, @"^(?<inner>.+?)\s*\*\s*(?<factor>\d+)$");
        if (multiplied.Success)
        {
            MemberCount inner = Count(multiplied.Groups["inner"].Value, unresolved);
            return inner with { Multiply = int.Parse(multiplied.Groups["factor"].Value), Expression = trimmed };
        }
        string source = trimmed switch
        {
            "VTStacks.Num()" => "VirtualTextureStacks",
            "NumVirtualTextures" => "Textures:Virtual",
            "NumSparseVolumeTextures" => "Textures:SparseVolume",
            "UniformVectorPreshaders.Num()" => "VectorPreshaders",
            "UniformScalarPreshaders.Num()" => "ScalarPreshaders",
            "UniformPreshaderBufferSize" => "PreshaderBufferSize",
            _ => string.Empty,
        };
        if (source.Length == 0)
        {
            unresolved.Add("count: " + trimmed);
            return new MemberCount("Unknown", 1, 1, trimmed);
        }
        return new MemberCount(source, 1, 1, trimmed);
    }

    /// <summary>
    /// What the engine aligns a uniform buffer's struct and its resource slots to. Stated here
    /// rather than assumed, because "a resource takes eight bytes" is a statement about the
    /// engine's own pointer size and not about any material.
    /// </summary>
    private static int Alignment(string engineRoot, string macro, List<string> unresolved)
    {
        string value = string.Empty;
        foreach (string relative in RelativeAlignmentHeaders)
        {
            string file = Path.Combine(engineRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                continue;
            }
            Match defined = Regex.Match(File.ReadAllText(file), @"#define[ \t]+" + macro + @"[ \t]+(?<value>[^\r\n]+)");
            if (defined.Success)
            {
                value = defined.Groups["value"].Value.Trim();
                break;
            }
        }
        if (value.Length == 0)
        {
            unresolved.Add("alignment not found: " + macro);
            return 0;
        }
        if (int.TryParse(value, out int literal))
        {
            return literal;
        }
        Match sized = Regex.Match(value, @"sizeof\s*\(\s*(?<type>\w+)\s*\)");
        if (sized.Success)
        {
            return sized.Groups["type"].Value switch
            {
                "uint64" or "int64" or "double" => 8,
                "uint32" or "int32" or "float" => 4,
                _ => 0,
            };
        }
        unresolved.Add("alignment unreadable: " + macro + " = " + value);
        return 0;
    }

    /// <summary>The kinds of texture parameter this engine knows, in the order it numbers them.</summary>
    private static IReadOnlyList<string> TextureTypes(string enumFile)
    {
        if (!File.Exists(enumFile))
        {
            return Array.Empty<string>();
        }
        Match block = EnumBlock.Match(File.ReadAllText(enumFile));
        if (!block.Success)
        {
            return Array.Empty<string>();
        }
        List<string> kinds = new();
        foreach (string entry in block.Groups["body"].Value.Split(','))
        {
            string name = entry.Split("//")[0].Trim();
            if (name.Length == 0 || name == "Count")
            {
                continue;
            }
            kinds.Add(name);
        }
        return kinds;
    }
}
