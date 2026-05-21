using Ruri.UEShaderTpkDumper.Core;

namespace Ruri.UEShaderTpkDumper.Parser;

// Walks a parsed `StructBlock`'s members and computes byte offsets +
// canonical resource list — the input shape that
// `FRHIUniformBufferLayoutInitializer::ComputeHash` consumes.
//
// Mirrors the Python generator's `compute_layout` pass. Key invariants:
//   * Numeric members align to their natural alignment (matching the C++
//     compiler's behaviour). Vec3 is sized 12 with alignment 16 — the
//     padding after it is 4.
//   * Resources occupy 8 bytes (pointer-sized) regardless of HLSL type.
//   * Arrays use `ARRAY_ELEM_ALIGN` (16) per element — so a `float[5]`
//     occupies 80 bytes (16 per scalar slot, packed via FUintVector4).
//   * Nested structs (SHADER_PARAMETER_STRUCT) recursively walk a child
//     struct's layout — Python does this; C# port stubs the recursion at
//     "look up by struct name in a registry" because we don't always have
//     the include graph wired here. For UBs that contain nested structs
//     the recursive walker is required.
public sealed class ResolvedResource
{
    public required int Offset;
    public required string Ubmt;     // UBMT slot name without UBMT_ prefix
    public required string Name;
    public required int ResourceIndex;  // assigned post-sort
    // HLSL/CPP-side type signature as it appears in
    // `SHADER_PARAMETER_TEXTURE(Texture3D<uint4>, …)` etc. Used by the
    // runtime symbol resolver to pick a real name when the SRT didn't
    // record an entry but the engine UB has a type-unique resource (e.g.
    // `Texture3D<uint4>` -> View.VolumetricLightmapIndirectionTexture).
    // May be empty for the few macros that don't carry a type token
    // (e.g. RENDER_TARGET_BINDING_SLOTS).
    public string ShaderType { get; set; } = string.Empty;
}

public sealed class LayoutResult
{
    public required string Name;
    // Mutable: a downstream IMPLEMENT_*_STRUCT scan swaps in the actual
    // shader binding name (`"View"`, `"Material"`, …) replacing the
    // C++ struct name default.
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

        // Expand macro tables in the body BEFORE walking members. View etc.
        // declare hundreds of members via `VIEW_UNIFORM_BUFFER_MEMBER_TABLE(...)`
        // which we expand recursively into SHADER_PARAMETER lines first.
        string expandedBody = _macroTables.Count > 0
            ? MacroTableExpander.Expand(block.Body, _macroTables)
            : block.Body;

        var ctx = new WalkContext();
        WalkBlock(expandedBody, prefix: string.Empty, baseOffset: 0, ctx, result);

        // Round struct size up to STRUCT_ALIGN, matching the C++ packing rule.
        // UE's `FShaderParametersMetadata::ComputeFieldOffsets` does this
        // after the last member.
        result.Size = AlignUp(ctx.LocalNext, Core.UbmtTables.StructAlign);
        // Assign resource indices in canonical sort order: (offset, ubmt).
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
                // SHADER_PARAMETER_STRUCT_INCLUDE inlines the included struct's
                // members at the current offset (no padding/no name prefix).
                if (_structRegistry.TryGetValue(line.CppType, out StructBlock inner))
                {
                    WalkBlock(inner.Body, prefix, baseOffset + ctx.LocalNext, ctx, result);
                }
            }
            else if (line.Ubmt == "NESTED_STRUCT")
            {
                // SHADER_PARAMETER_STRUCT(Type, Name) — child struct padded to
                // its own alignment (16). Names prefixed with `Name_`.
                if (_structRegistry.TryGetValue(line.CppType, out StructBlock inner))
                {
                    ctx.LocalNext = AlignUp(ctx.LocalNext, Core.UbmtTables.StructAlign);
                    int childBase = baseOffset + ctx.LocalNext;
                    // Walk child into a temp context to know its size, then
                    // bump LocalNext by that size.
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
            // 4-per-vec4 packing — the array gets ceil(N/4) FUintVector4 slots.
            cppType = packedType;
            arrayN = (arrayN + 3) / 4;
        }

        if (!TypeTable.Table.TryGetValue(cppType, out NumericTypeInfo info))
        {
            // Unknown type — skip silently. Common for game-specific structs
            // that the Python generator also skips with a warning.
            return;
        }

        if (arrayN > 0)
        {
            // Array element stride is max(natural-align, ARRAY_ELEM_ALIGN).
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
                // Array members only emit ONE record (the parent), break.
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

    // Resolve `[N]` / `[Foo::Max]` / `[MyConstant]` to an integer count.
    // 0 means "no array".
    private int ResolveArraySize(string? arrayDecl)
    {
        if (string.IsNullOrEmpty(arrayDecl)) return 0;
        string inner = arrayDecl.Trim('[', ']').Trim();
        if (inner.Length == 0) return 0;
        if (int.TryParse(inner, out int direct)) return direct;
        // Try the constants table — may be `Foo::Max` style; we only match
        // the last identifier segment to mirror Python's lookup.
        string ident = inner;
        int sep = ident.LastIndexOf("::", StringComparison.Ordinal);
        if (sep >= 0) ident = ident[(sep + 2)..];
        if (_constants.TryGetValue(ident, out long v)) return (int)v;
        return 0;
    }

    private static int AlignUp(int x, int a) => (x + a - 1) & ~(a - 1);

    // Convert this layout into the (Offset, UbmtValue) list `ComputeLayoutHash`
    // expects. Resources sorted by (offset, ubmt) which is the canonical
    // FRHIUniformBufferLayoutInitializer::Resources order.
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
