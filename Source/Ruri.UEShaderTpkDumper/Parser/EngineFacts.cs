using System.Text.RegularExpressions;

namespace Ruri.UEShaderTpkDumper.Parser;

/// <summary>One numeric shader parameter type as the engine lays it out: how many 32-bit lanes, aligned to what.</summary>
public readonly record struct NumericTypeInfo(int Size, int Alignment, string Ubmt, string HlslName, int RowCount, int ColumnCount, bool IsMatrix);

/// <summary>
/// The facts about uniform buffers that only the ENGINE states, read out of one engine tree.
///
/// Every one of these used to be a table typed into this program from memory of one engine
/// version, and every one of them drifts: the base-type enum gains members, the hash gains
/// terms, a vector type is renamed, an alignment moves headers. A seed built from a table that
/// drifted hashes to a value no cook ever carries, and nothing downstream can tell that from
/// "this build modified the engine". So the tree itself is the only source: the enum in its
/// order, the alignments as defined, each parameter type's rows, columns and alignment as its
/// own specialisation declares them, and which shape of hash this engine computes.
/// </summary>
public sealed class EngineFacts
{
    /// <summary>The hash is size and static-slot index: what 4.x computes.</summary>
    public const string SizeAndStaticSlot = "SizeAndStaticSlot";

    /// <summary>The hash is size, binding flags and whether a static slot exists: 5.0 to 5.4.</summary>
    public const string SizeFlagsStatic = "SizeFlagsStatic";

    /// <summary>As above, plus the buffer's usage flag bits: 5.5 onward.</summary>
    public const string SizeFlagsStaticUsage = "SizeFlagsStaticUsage";

    private static readonly string[] BaseTypeHeaders =
    {
        "Engine/Source/Runtime/RHI/Public/RHIDefinitions.h",
    };

    private static readonly string[] AlignmentHeaders =
    {
        "Engine/Source/Runtime/RHI/Public/RHI.h",
        "Engine/Source/Runtime/RHI/Public/RHIDefinitions.h",
    };

    private static readonly string[] HashHeaders =
    {
        "Engine/Source/Runtime/RHI/Public/RHIUniformBufferLayoutInitializer.h",
        "Engine/Source/Runtime/RHI/Public/RHIResources.h",
    };

    private const string TypeInfoHeader = "Engine/Source/Runtime/RenderCore/Public/ShaderParameterMacros.h";
    private const string UsageFlagsHeader = "Engine/Source/Runtime/RenderCore/Public/ShaderParameterMetadata.h";

    private static readonly Regex TypeInfoBlock = new(
        @"struct\s+TShaderParameterTypeInfo<(?<type>[A-Za-z_][A-Za-z_0-9]*)>\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex UsageFlagsBlock = new(
        @"enum\s+class\s+EUsageFlags\s*:\s*\w+\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private EngineFacts(IReadOnlyDictionary<string, int> baseTypes, int structAlignment, int arrayElementAlignment,
        int pointerAlignment, IReadOnlyDictionary<string, NumericTypeInfo> numericTypes, string hashFormula,
        IReadOnlyDictionary<string, int> usageFlags, IReadOnlyList<string> unresolved)
    {
        UniformBufferBaseTypes = baseTypes;
        StructAlignment = structAlignment;
        ArrayElementAlignment = arrayElementAlignment;
        PointerAlignment = pointerAlignment;
        NumericTypes = numericTypes;
        HashFormula = hashFormula;
        UsageFlags = usageFlags;
        Unresolved = unresolved;
    }

    /// <summary>The engine's base-type enum, member name without its UBMT_ prefix to its value.</summary>
    public IReadOnlyDictionary<string, int> UniformBufferBaseTypes { get; }

    public int StructAlignment { get; }

    public int ArrayElementAlignment { get; }

    public int PointerAlignment { get; }

    /// <summary>Every C++ type the engine accepts as a numeric shader parameter, laid out as it declares it.</summary>
    public IReadOnlyDictionary<string, NumericTypeInfo> NumericTypes { get; }

    public string HashFormula { get; }

    /// <summary>The usage flags a 5.x buffer can declare, name to bit value; empty for an engine without them.</summary>
    public IReadOnlyDictionary<string, int> UsageFlags { get; }

    public IReadOnlyList<string> Unresolved { get; }

    /// <summary>
    /// The bit an engine's own scalar array packing puts a scalar type in: four scalars to one
    /// register, the packed type being the four-lane vector of the same base type. Stated by the
    /// engine's TShaderParameterScalarArrayTypeInfo; carried here as the one rule that is code
    /// rather than a declaration.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ScalarArrayPack = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["uint32"] = "FUintVector4",
        ["uint"] = "FUintVector4",
        ["int32"] = "FIntVector4",
        ["int"] = "FIntVector4",
        ["float"] = "FVector4f",
    };

    /// <param name="sourceFiles">Every source file of the tree: a type's layout is declared wherever the type lives, not in one header.</param>
    public static EngineFacts Read(string engineRoot, IReadOnlyList<string> sourceFiles)
    {
        List<string> unresolved = new();
        IReadOnlyDictionary<string, int> baseTypes = BaseTypes(engineRoot, unresolved);
        int structAlignment = Alignment(engineRoot, "SHADER_PARAMETER_STRUCT_ALIGNMENT", unresolved);
        int arrayElementAlignment = Alignment(engineRoot, "SHADER_PARAMETER_ARRAY_ELEMENT_ALIGNMENT", unresolved);
        int pointerAlignment = Alignment(engineRoot, "SHADER_PARAMETER_POINTER_ALIGNMENT", unresolved);
        IReadOnlyDictionary<string, NumericTypeInfo> numericTypes = NumericTypeTable(sourceFiles, unresolved);
        string hashFormula = HashFormulaOf(engineRoot, unresolved);
        IReadOnlyDictionary<string, int> usageFlags = UsageFlagValues(engineRoot);
        return new EngineFacts(baseTypes, structAlignment, arrayElementAlignment, pointerAlignment,
            numericTypes, hashFormula, usageFlags, unresolved);
    }

    /// <summary>The value of one base type by its name with or without the UBMT_ prefix.</summary>
    public int BaseType(string name)
    {
        string key = name.StartsWith("UBMT_", StringComparison.Ordinal) ? name["UBMT_".Length..] : name;
        if (UniformBufferBaseTypes.TryGetValue(key, out int value))
        {
            return value;
        }
        throw new KeyNotFoundException($"the engine's EUniformBufferBaseType has no member '{name}'");
    }

    private static IReadOnlyDictionary<string, int> BaseTypes(string engineRoot, List<string> unresolved)
    {
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        foreach (string relative in BaseTypeHeaders)
        {
            string file = Combine(engineRoot, relative);
            if (!File.Exists(file))
            {
                continue;
            }
            Match block = Regex.Match(UeSourceScanner.StripComments(File.ReadAllText(file)),
                @"enum\s+EUniformBufferBaseType\s*(?::\s*\w+)?\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
            if (!block.Success)
            {
                continue;
            }
            int next = 0;
            foreach (string entry in block.Groups["body"].Value.Split(','))
            {
                string member = entry.Trim();
                if (member.Length == 0)
                {
                    continue;
                }
                int equals = member.IndexOf('=');
                string name = (equals >= 0 ? member[..equals] : member).Trim();
                if (!name.StartsWith("UBMT_", StringComparison.Ordinal))
                {
                    continue;
                }
                if (equals >= 0 && ConstantsCollector.TryEvaluate(member[(equals + 1)..], values, out long stated))
                {
                    next = (int)stated;
                }
                values[name["UBMT_".Length..]] = next;
                next++;
            }
            return values;
        }
        unresolved.Add("EUniformBufferBaseType not found");
        return values;
    }

    private static int Alignment(string engineRoot, string macro, List<string> unresolved)
    {
        foreach (string relative in AlignmentHeaders)
        {
            string file = Combine(engineRoot, relative);
            if (!File.Exists(file))
            {
                continue;
            }
            Match defined = Regex.Match(File.ReadAllText(file), @"#define[ \t]+" + macro + @"[ \t]+(?<value>[^\r\n]+)");
            if (!defined.Success)
            {
                continue;
            }
            string value = defined.Groups["value"].Value.Trim();
            if (int.TryParse(value, out int literal))
            {
                return literal;
            }
            Match sized = Regex.Match(value, @"sizeof\s*\(\s*(?<type>\w+)\s*\)");
            if (sized.Success)
            {
                switch (sized.Groups["type"].Value)
                {
                    case "uint64" or "int64" or "double": return 8;
                    case "uint32" or "int32" or "float": return 4;
                }
            }
            unresolved.Add("alignment unreadable: " + macro + " = " + value);
            return 0;
        }
        unresolved.Add("alignment not found: " + macro);
        return 0;
    }

    /// <summary>
    /// Every numeric type's layout as its TShaderParameterTypeInfo specialisation declares it. The
    /// engine states rows and columns in its own convention -- a vector is one row of N columns in
    /// 5.x -- while the seed's readers take a vector's lane count as its row count, so a type with
    /// a single row or a single column is a vector of the larger count, and anything else a matrix.
    /// </summary>
    private static IReadOnlyDictionary<string, NumericTypeInfo> NumericTypeTable(IReadOnlyList<string> sourceFiles, List<string> unresolved)
    {
        Dictionary<string, NumericTypeInfo> table = new(StringComparer.Ordinal);
        foreach (string file in sourceFiles)
        {
            string raw;
            try { raw = File.ReadAllText(file); }
            catch { continue; }
            if (!raw.Contains("TShaderParameterTypeInfo<", StringComparison.Ordinal))
            {
                continue;
            }
            AddSpecialisations(UeSourceScanner.StripComments(raw), table);
        }
        AddSpelling(table, "int", "int32");
        AddSpelling(table, "uint", "uint32");
        AddSpelling(table, "unsigned", "uint32");
        if (table.Count == 0)
        {
            unresolved.Add("no TShaderParameterTypeInfo specialisations found");
        }
        return table;
    }

    private static void AddSpecialisations(string text, Dictionary<string, NumericTypeInfo> table)
    {
        foreach (Match block in TypeInfoBlock.Matches(text))
        {
            string type = block.Groups["type"].Value;
            string body = block.Groups["body"].Value;
            Match baseType = Regex.Match(body, @"BaseType\s*=\s*UBMT_(?<name>\w+)");
            Match rows = Regex.Match(body, @"NumRows\s*=\s*(?<n>\d+)");
            Match columns = Regex.Match(body, @"NumColumns\s*=\s*(?<n>\d+)");
            Match alignment = Regex.Match(body, @"Alignment\s*=\s*(?<n>\d+)");
            if (!baseType.Success || !rows.Success || !columns.Success || !alignment.Success)
            {
                continue;
            }
            int engineRows = int.Parse(rows.Groups["n"].Value);
            int engineColumns = int.Parse(columns.Groups["n"].Value);
            string ubmt = baseType.Groups["name"].Value;
            bool isMatrix = engineRows > 1 && engineColumns > 1;
            int rowCount = isMatrix ? engineRows : Math.Max(engineRows, engineColumns);
            int columnCount = isMatrix ? engineColumns : 1;
            table[type] = new NumericTypeInfo(
                Size: 4 * engineRows * engineColumns,
                Alignment: int.Parse(alignment.Groups["n"].Value),
                Ubmt: ubmt,
                HlslName: HlslName(ubmt, rowCount, columnCount, isMatrix),
                RowCount: rowCount,
                ColumnCount: columnCount,
                IsMatrix: isMatrix);
        }
    }

    private static void AddSpelling(Dictionary<string, NumericTypeInfo> table, string alias, string declared)
    {
        if (!table.ContainsKey(alias) && table.TryGetValue(declared, out NumericTypeInfo info))
        {
            table[alias] = info;
        }
    }

    private static string HlslName(string ubmt, int rows, int columns, bool isMatrix)
    {
        string scalar = ubmt switch
        {
            "FLOAT32" => "Float",
            "INT32" => "Int",
            "UINT32" => "UInt",
            "BOOL" => "Bool",
            _ => ubmt,
        };
        if (isMatrix)
        {
            return $"{scalar}{rows}x{columns}";
        }
        return rows > 1 ? $"{scalar}{rows}" : scalar;
    }

    /// <summary>
    /// Which hash this engine computes for a layout, read off its own ComputeHash: one that
    /// mentions the usage flags has the 5.5 shape, one that mentions the binding flags the 5.0
    /// shape, and one that mentions neither folds the static slot's index in as 4.x does.
    /// </summary>
    private static string HashFormulaOf(string engineRoot, List<string> unresolved)
    {
        foreach (string relative in HashHeaders)
        {
            string file = Combine(engineRoot, relative);
            if (!File.Exists(file))
            {
                continue;
            }
            string text = File.ReadAllText(file);
            int at = text.IndexOf("void ComputeHash()", StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }
            int end = text.IndexOf("Hash = TmpHash;", at, StringComparison.Ordinal);
            string body = end > at ? text[at..end] : text[at..];
            if (body.Contains("NoEmulatedUniformBuffer", StringComparison.Ordinal))
            {
                return SizeFlagsStaticUsage;
            }
            if (body.Contains("BindingFlags", StringComparison.Ordinal))
            {
                return SizeFlagsStatic;
            }
            if (body.Contains("StaticSlot", StringComparison.Ordinal))
            {
                return SizeAndStaticSlot;
            }
        }
        unresolved.Add("uniform buffer layout ComputeHash not found");
        return string.Empty;
    }

    private static IReadOnlyDictionary<string, int> UsageFlagValues(string engineRoot)
    {
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        string file = Combine(engineRoot, UsageFlagsHeader);
        if (!File.Exists(file))
        {
            return values;
        }
        Match block = UsageFlagsBlock.Match(UeSourceScanner.StripComments(File.ReadAllText(file)));
        if (!block.Success)
        {
            return values;
        }
        foreach (string entry in block.Groups["body"].Value.Split(','))
        {
            string member = entry.Trim();
            int equals = member.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }
            string name = member[..equals].Trim();
            if (ConstantsCollector.TryEvaluate(member[(equals + 1)..], values, out long value))
            {
                values[name] = (int)value;
            }
        }
        return values;
    }

    private static string Combine(string engineRoot, string relative)
        => Path.Combine(engineRoot, relative.Replace('/', Path.DirectorySeparatorChar));
}
