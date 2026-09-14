using Ruri.UEShaderTpkDumper.Core;

namespace Ruri.UEShaderTpkDumper.Parser;

public sealed class ResolvedResource
{
    public required int Offset;
    public required string Ubmt;
    public required string Name;
    public required int ResourceIndex;
    public string ShaderType { get; set; } = string.Empty;
}

public sealed class LayoutResult
{
    public required string Name;
    public required string BindingName { get; set; }
    public required string Kind;
    public required int Size { get; set; }
    public required List<NumericMember> NumericMembers = new();
    public required List<ResolvedResource> Resources = new();
}

public sealed class NumericMember
{
    public required string Name;
    public required int Offset;
    public required int Size;
    public required string HlslType;
    public required string Ubmt;
    public required int RowCount;
    public required int ColumnCount;
    public required bool IsMatrix;
    public required int ArraySize;
}

/// <summary>
/// One uniform buffer struct laid out as the engine lays it out: members in declaration order,
/// each aligned as its type's own specialisation says, resources one pointer slot each, arrays
/// one element-alignment stride each, the whole rounded up to the struct alignment.
///
/// A member whose type the engine does not declare a layout for, or whose array size names a
/// constant the tree does not define, stops the walk: a seed missing one member hashes to a
/// value no cook carries and reads as "engine modified", which is worse than no seed at all.
/// </summary>
public sealed class LayoutWalker
{
    private readonly EngineFacts facts;
    private readonly IReadOnlyDictionary<string, long> constants;
    private readonly Dictionary<string, StructBlock> structRegistry;
    private readonly IReadOnlyDictionary<string, MacroTableExpander.TableEntry> macroTables;

    public LayoutWalker(
        EngineFacts facts,
        IReadOnlyDictionary<string, long> constants,
        Dictionary<string, StructBlock> structRegistry,
        IReadOnlyDictionary<string, MacroTableExpander.TableEntry>? macroTables = null)
    {
        this.facts = facts;
        this.constants = constants;
        this.structRegistry = structRegistry;
        this.macroTables = macroTables ?? new Dictionary<string, MacroTableExpander.TableEntry>();
    }

    public LayoutResult Walk(StructBlock block)
    {
        LayoutResult result = new()
        {
            Name = block.CppName,
            BindingName = block.BindingName,
            Kind = block.Kind,
            Size = 0,
            NumericMembers = new(),
            Resources = new(),
        };

        string expandedBody = macroTables.Count > 0
            ? MacroTableExpander.Expand(block.Body, macroTables)
            : block.Body;

        WalkContext context = new();
        WalkBlock(expandedBody, prefix: string.Empty, baseOffset: 0, context, result);

        result.Size = AlignUp(context.LocalNext, facts.StructAlignment);
        result.Resources.Sort((a, b) =>
        {
            int byOffset = a.Offset.CompareTo(b.Offset);
            return byOffset != 0 ? byOffset : string.CompareOrdinal(a.Ubmt, b.Ubmt);
        });
        for (int i = 0; i < result.Resources.Count; i++)
        {
            result.Resources[i].ResourceIndex = i;
        }
        return result;
    }

    private sealed class WalkContext
    {
        public int LocalNext;
    }

    private void WalkBlock(string body, string prefix, int baseOffset, WalkContext context, LayoutResult result)
    {
        foreach (MemberLine line in MemberLineParser.ParseBody(body))
        {
            if (line.IsResource)
            {
                AddResource(line, prefix, baseOffset, context, result);
            }
            else if (line.Ubmt is "INCLUDED_STRUCT" or "NESTED_STRUCT")
            {
                // Either way the member is a C++ object of the inner struct type, aligned and
                // sized as a struct; the two differ only in whether its members keep its name.
                // Walking an included struct on the parent's own running offset counted that
                // offset twice -- every member after the first include landed past the end.
                StructBlock inner = Registered(line);
                context.LocalNext = AlignUp(context.LocalNext, facts.StructAlignment);
                int childBase = baseOffset + context.LocalNext;
                string childPrefix = line.Ubmt == "NESTED_STRUCT" ? prefix + line.Name + "_" : prefix;
                WalkContext childContext = new();
                WalkBlock(inner.Body, childPrefix, childBase, childContext, result);
                context.LocalNext += AlignUp(childContext.LocalNext, facts.StructAlignment);
            }
            else
            {
                AddNumeric(line, prefix, baseOffset, context, result);
            }
        }
    }

    /// <summary>The declared block of a nested struct, by the type's own name whichever namespace the member spelled it in.</summary>
    private StructBlock Registered(MemberLine line)
    {
        string type = line.CppType;
        int scope = type.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0)
        {
            type = type[(scope + 2)..];
        }
        if (structRegistry.TryGetValue(type, out StructBlock inner))
        {
            return inner;
        }
        throw new InvalidOperationException($"member '{line.Name}' nests struct '{line.CppType}', which no BEGIN_*_STRUCT in the tree declares");
    }

    private void AddResource(MemberLine line, string prefix, int baseOffset, WalkContext context, LayoutResult result)
    {
        int slot = facts.PointerAlignment;
        int count = Math.Max(1, ResolveArraySize(line.ArrayDecl, line.Name));
        context.LocalNext = AlignUp(context.LocalNext, slot);
        for (int i = 0; i < count; i++)
        {
            result.Resources.Add(new ResolvedResource
            {
                Name = prefix + line.Name,
                Ubmt = line.Ubmt,
                Offset = baseOffset + context.LocalNext + i * slot,
                ResourceIndex = 0,
                ShaderType = line.ShaderType ?? string.Empty,
            });
        }
        context.LocalNext += slot * count;
    }

    private void AddNumeric(MemberLine line, string prefix, int baseOffset, WalkContext context, LayoutResult result)
    {
        string cppType = line.CppType;
        int arrayCount = ResolveArraySize(line.ArrayDecl, line.Name);
        if (string.Equals(line.Macro, "SHADER_PARAMETER_SCALAR_ARRAY", StringComparison.Ordinal)
            && EngineFacts.ScalarArrayPack.TryGetValue(cppType, out string? packedType))
        {
            cppType = packedType;
            arrayCount = (arrayCount + 3) / 4;
        }

        if (!facts.NumericTypes.TryGetValue(cppType, out NumericTypeInfo info))
        {
            throw new InvalidOperationException($"member '{line.Name}' has type '{cppType}', which the engine declares no shader parameter layout for");
        }

        if (arrayCount > 0)
        {
            // An array element occupies its own size rounded up to the element alignment: a
            // scalar takes a whole register, a matrix its four. Striding by the alignment alone
            // packed every matrix array to a quarter of its length.
            int stride = AlignUp(info.Size, facts.ArrayElementAlignment);
            context.LocalNext = AlignUp(context.LocalNext, facts.ArrayElementAlignment);
            result.NumericMembers.Add(new NumericMember
            {
                Name = prefix + line.Name,
                Offset = baseOffset + context.LocalNext,
                Size = info.Size,
                HlslType = info.HlslName,
                Ubmt = info.Ubmt,
                RowCount = info.RowCount,
                ColumnCount = info.ColumnCount,
                IsMatrix = info.IsMatrix,
                ArraySize = arrayCount,
            });
            context.LocalNext += stride * arrayCount;
            return;
        }

        context.LocalNext = AlignUp(context.LocalNext, info.Alignment);
        result.NumericMembers.Add(new NumericMember
        {
            Name = prefix + line.Name,
            Offset = baseOffset + context.LocalNext,
            Size = info.Size,
            HlslType = info.HlslName,
            Ubmt = info.Ubmt,
            RowCount = info.RowCount,
            ColumnCount = info.ColumnCount,
            IsMatrix = info.IsMatrix,
            ArraySize = 0,
        });
        context.LocalNext += info.Size;
    }

    /// <summary>How many elements an array declaration names, or zero for no array; a name the tree does not define stops the walk.</summary>
    private int ResolveArraySize(string? arrayDecl, string memberName)
    {
        if (string.IsNullOrEmpty(arrayDecl))
        {
            return 0;
        }
        string inner = arrayDecl.Trim('[', ']').Trim();
        if (inner.Length == 0)
        {
            return 0;
        }
        if (ConstantsCollector.TryEvaluate(inner, constants, out long value) && value > 0)
        {
            return (int)value;
        }
        throw new InvalidOperationException($"member '{memberName}' is an array sized by '{inner}', which the tree does not define as an integer");
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);

    public static List<ComputeLayoutHash.Resource> ToHashResources(LayoutResult layout, EngineFacts facts)
    {
        List<ComputeLayoutHash.Resource> resources = new(layout.Resources.Count);
        foreach (ResolvedResource resource in layout.Resources)
        {
            resources.Add(new ComputeLayoutHash.Resource(resource.Offset, facts.BaseType(resource.Ubmt)));
        }
        return resources;
    }
}
