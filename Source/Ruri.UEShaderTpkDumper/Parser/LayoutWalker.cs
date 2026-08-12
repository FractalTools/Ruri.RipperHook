using Ruri.UEShaderTpkDumper.Core;

namespace Ruri.UEShaderTpkDumper.Parser;

public sealed class ResolvedResource
{
    public required int Offset;
    public required string Ubmt;    public required string Name;
    public required int ResourceIndex;    public string ShaderType { get; set; } = string.Empty;
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

public sealed class LayoutWalker
{
    private readonly IReadOnlyDictionary<string, int> _ubmtTable;
    private readonly IReadOnlyDictionary<string, long> _constants;
    private readonly Dictionary<string, StructBlock> _structRegistry;
    private readonly IReadOnlyDictionary<string, MacroTableExpander.TableEntry> _macroTables;

    public LayoutWalker(
        IReadOnlyDictionary<string, int> ubmtTable,
        IReadOnlyDictionary<string, long> constants,
        Dictionary<string, StructBlock> structRegistry,
        IReadOnlyDictionary<string, MacroTableExpander.TableEntry>? macroTables = null)
    {
        _ubmtTable = ubmtTable;
        _constants = constants;
        _structRegistry = structRegistry;
        _macroTables = macroTables ?? new Dictionary<string, MacroTableExpander.TableEntry>();
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

        string expandedBody = _macroTables.Count > 0
            ? MacroTableExpander.Expand(block.Body, _macroTables)
            : block.Body;

        var ctx = new WalkContext();
        WalkBlock(expandedBody, prefix: string.Empty, baseOffset: 0, ctx, result);

        result.Size = AlignUp(ctx.LocalNext, Core.UbmtTables.StructAlign);
        result.Resources.Sort((a, b) =>
        {
            int cmp = a.Offset.CompareTo(b.Offset);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Ubmt, b.Ubmt);
        });
        for (int i = 0; i < result.Resources.Count; i++) result.Resources[i].ResourceIndex = i;
        return result;
    }

    private sealed class WalkContext
    {
        public int LocalNext;
    }

    private void WalkBlock(string body, string prefix, int baseOffset, WalkContext ctx, LayoutResult result)
    {
        foreach (MemberLine line in MemberLineParser.ParseBody(body))
        {
            if (line.IsResource)
            {
                AddResource(line, prefix, baseOffset, ctx, result);
            }
            else if (line.Ubmt == "INCLUDED_STRUCT")
            {
                if (_structRegistry.TryGetValue(line.CppType, out StructBlock inner))
                {
                    WalkBlock(inner.Body, prefix, baseOffset + ctx.LocalNext, ctx, result);
                }
            }
            else if (line.Ubmt == "NESTED_STRUCT")
            {
                if (_structRegistry.TryGetValue(line.CppType, out StructBlock inner))
                {
                    ctx.LocalNext = AlignUp(ctx.LocalNext, Core.UbmtTables.StructAlign);
                    int childBase = baseOffset + ctx.LocalNext;
                    var childCtx = new WalkContext();
                    WalkBlock(inner.Body, prefix + line.Name + "_", childBase, childCtx, result);
                    ctx.LocalNext += AlignUp(childCtx.LocalNext, Core.UbmtTables.StructAlign);
                }
            }
            else
            {
                AddNumeric(line, prefix, baseOffset, ctx, result);
            }
        }
    }

    private void AddResource(MemberLine line, string prefix, int baseOffset, WalkContext ctx, LayoutResult result)
    {
        int align = Core.UbmtTables.PointerAlign;
        int elemSize = Core.UbmtTables.PointerAlign;
        int arrayN = ResolveArraySize(line.ArrayDecl);

        if (arrayN > 0)
        {
            ctx.LocalNext = AlignUp(ctx.LocalNext, align);
            for (int i = 0; i < arrayN; i++)
            {
                int off = baseOffset + ctx.LocalNext + i * elemSize;
                result.Resources.Add(new ResolvedResource
                {
                    Name = prefix + line.Name,
                    Ubmt = line.Ubmt,
                    Offset = off,
                    ResourceIndex = 0,
                    ShaderType = line.ShaderType ?? string.Empty,
                });
            }
            ctx.LocalNext += elemSize * arrayN;
        }
        else
        {
            ctx.LocalNext = AlignUp(ctx.LocalNext, align);
            int off = baseOffset + ctx.LocalNext;
            result.Resources.Add(new ResolvedResource
            {
                Name = prefix + line.Name,
                Ubmt = line.Ubmt,
                Offset = off,
                ResourceIndex = 0,
                ShaderType = line.ShaderType ?? string.Empty,
            });
            ctx.LocalNext += elemSize;
        }
    }

    private void AddNumeric(MemberLine line, string prefix, int baseOffset, WalkContext ctx, LayoutResult result)
    {
        string cppType = line.CppType;
        int arrayN = ResolveArraySize(line.ArrayDecl);
        bool isScalarArrayMacro = string.Equals(line.Macro, "SHADER_PARAMETER_SCALAR_ARRAY", StringComparison.Ordinal);
        if (isScalarArrayMacro && TypeTable.ScalarArrayPack.TryGetValue(cppType, out string? packedType))
        {
            cppType = packedType;
            arrayN = (arrayN + 3) / 4;
        }

        if (!TypeTable.Table.TryGetValue(cppType, out NumericTypeInfo info))
        {
            return;
        }

        if (arrayN > 0)
        {
            int elemStride = Math.Max(info.Alignment, Core.UbmtTables.ArrayElemAlign);
            ctx.LocalNext = AlignUp(ctx.LocalNext, elemStride);
            for (int i = 0; i < arrayN; i++)
            {
                int off = baseOffset + ctx.LocalNext + i * elemStride;
                result.NumericMembers.Add(new NumericMember
                {
                    Name = prefix + line.Name + (arrayN > 0 && i > 0 ? "" : ""),
                    Offset = off,
                    Size = info.Size,
                    HlslType = info.HlslName,
                    Ubmt = info.Ubmt,
                    RowCount = info.RowCount,
                    ColumnCount = info.ColumnCount,
                    IsMatrix = info.IsMatrix,
                    ArraySize = arrayN,
                });
                break;
            }
            ctx.LocalNext += elemStride * arrayN;
        }
        else
        {
            ctx.LocalNext = AlignUp(ctx.LocalNext, info.Alignment);
            int off = baseOffset + ctx.LocalNext;
            result.NumericMembers.Add(new NumericMember
            {
                Name = prefix + line.Name,
                Offset = off,
                Size = info.Size,
                HlslType = info.HlslName,
                Ubmt = info.Ubmt,
                RowCount = info.RowCount,
                ColumnCount = info.ColumnCount,
                IsMatrix = info.IsMatrix,
                ArraySize = 0,
            });
            ctx.LocalNext += info.Size;
        }
    }

    private int ResolveArraySize(string? arrayDecl)
    {
        if (string.IsNullOrEmpty(arrayDecl)) return 0;
        string inner = arrayDecl.Trim('[', ']').Trim();
        if (inner.Length == 0) return 0;
        if (int.TryParse(inner, out int direct)) return direct;
        string ident = inner;
        int sep = ident.LastIndexOf("::", StringComparison.Ordinal);
        if (sep >= 0) ident = ident[(sep + 2)..];
        if (_constants.TryGetValue(ident, out long v)) return (int)v;
        return 0;
    }

    private static int AlignUp(int x, int a) => (x + a - 1) & ~(a - 1);

    public static List<ComputeLayoutHash.Resource> ToHashResources(LayoutResult layout, IReadOnlyDictionary<string, int> ubmtTable)
    {
        List<ComputeLayoutHash.Resource> resources = new(layout.Resources.Count);
        foreach (ResolvedResource r in layout.Resources)
        {
            int ubmtValue = Core.UbmtTables.Resolve(ubmtTable, r.Ubmt);
            resources.Add(new ComputeLayoutHash.Resource(r.Offset, ubmtValue));
        }
        return resources;
    }
}
