namespace Ruri.UEShaderTpkDumper.Parser;

// C++ numeric type → (size, alignment, UBMT name, HLSL name, RowCount, ColumnCount).
//
// `Size` is `sizeof(T)`: NOT padded to alignment. MS_ALIGN/GCC_ALIGN modifiers
// (used by `TAlignedTypedef<T, A>::Type`) raise alignof but don't inflate
// sizeof; e.g. an alignas(16) FVector3f still has sizeof=12. The next member
// is aligned to its own type's alignment, so size and alignment matter
// independently — this matches what compilers actually do for cooked shipping
// UE structs, and what the layout hash discriminator XOR-folds.
//
// RowCount/ColumnCount convention follows the PROJECT'S `NumericShaderParameter`
// convention (canonical pattern in `MaterialConstantBufferReader.AddVectorMember`,
// downstream consumer at `LayoutBuilder.cs::TryCreateLogicalTypeFromMetadata`):
//   * Scalar:   RowCount=1,  ColumnCount=1
//   * Vector N: RowCount=N,  ColumnCount=1   (N=2/3/4 — RowCount is the component count)
//   * Matrix:   RowCount=R,  ColumnCount=C   (paired with IsMatrix=True)
// Using the HLSL row/col semantic instead (RowCount=1, ColumnCount=N) would
// silently collapse every vector field to a scalar in the rewriter.
public readonly record struct NumericTypeInfo(int Size, int Alignment, string Ubmt, string HlslName, int RowCount, int ColumnCount, bool IsMatrix);

public static class TypeTable
{
    public static readonly IReadOnlyDictionary<string, NumericTypeInfo> Table = new Dictionary<string, NumericTypeInfo>(StringComparer.Ordinal)
    {
        ["bool"]          = new( 4,  4, "BOOL",    "Bool",     1, 1, false),
        ["uint32"]        = new( 4,  4, "UINT32",  "UInt",     1, 1, false),
        ["int32"]         = new( 4,  4, "INT32",   "Int",      1, 1, false),
        ["int"]           = new( 4,  4, "INT32",   "Int",      1, 1, false),
        ["uint"]          = new( 4,  4, "UINT32",  "UInt",     1, 1, false),
        ["float"]         = new( 4,  4, "FLOAT32", "Float",    1, 1, false),
        ["FVector2f"]     = new( 8,  8, "FLOAT32", "Float2",   2, 1, false),
        ["FVector3f"]     = new(12, 16, "FLOAT32", "Float3",   3, 1, false),
        ["FVector4f"]     = new(16, 16, "FLOAT32", "Float4",   4, 1, false),
        ["FLinearColor"]  = new(16, 16, "FLOAT32", "Float4",   4, 1, false),
        ["FIntPoint"]     = new( 8,  8, "INT32",   "Int2",     2, 1, false),
        ["FUintVector2"]  = new( 8,  8, "UINT32",  "UInt2",    2, 1, false),
        ["FIntVector"]    = new(12, 16, "INT32",   "Int3",     3, 1, false),
        ["FUintVector3"]  = new(12, 16, "UINT32",  "UInt3",    3, 1, false),
        ["FIntVector4"]   = new(16, 16, "INT32",   "Int4",     4, 1, false),
        ["FUintVector4"]  = new(16, 16, "UINT32",  "UInt4",    4, 1, false),
        ["FIntRect"]      = new(16, 16, "INT32",   "Int4",     4, 1, false),
        ["FQuat4f"]       = new(16, 16, "FLOAT32", "Float4",   4, 1, false),
        ["FMatrix44f"]    = new(64, 16, "FLOAT32", "Float4x4", 4, 4, true),
        ["FMatrix3x4f"]   = new(48, 16, "FLOAT32", "Float3x4", 3, 4, true),
        ["FMatrix44d"]    = new(64, 16, "FLOAT32", "Float4x4", 4, 4, true),
        // LWC types — same layout as their float counterparts post-cooking.
        ["FVector"]       = new(12, 16, "FLOAT32", "Float3",   3, 1, false),
        ["FVector4"]      = new(16, 16, "FLOAT32", "Float4",   4, 1, false),
        ["FMatrix"]       = new(64, 16, "FLOAT32", "Float4x4", 4, 4, true),
        ["FMatrix3x4"]    = new(48, 16, "FLOAT32", "Float3x4", 3, 4, true),
        ["FMatrix44"]     = new(64, 16, "FLOAT32", "Float4x4", 4, 4, true),
    };

    // SHADER_PARAMETER_SCALAR_ARRAY(T, name, [N]) packs scalars 4-per-vec4 using
    // these substitute types. Mirrors `TShaderParameterScalarArrayTypeInfo`
    // (ShaderParameterMacros.h ~1900-1910).
    public static readonly IReadOnlyDictionary<string, string> ScalarArrayPack = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["uint32"] = "FUintVector4",
        ["uint"]   = "FUintVector4",
        ["int32"]  = "FIntVector4",
        ["int"]    = "FIntVector4",
        ["float"]  = "FVector4f",
    };

    // Resource-pointer pseudo-type (SHADER_PARAMETER_TEXTURE / SRV / UAV /
    // SAMPLER / RDG_*). sizeof = 8 (pointer), alignment = 8.
    public const int ResourceSize = UbmtTablesAlignment.PointerAlign;
    public const int ResourceAlign = UbmtTablesAlignment.PointerAlign;
}

// Tiny alias so consumers don't need to spell out the UbmtTables type just
// to grab alignment constants.
internal static class UbmtTablesAlignment
{
    public const int PointerAlign = Core.UbmtTables.PointerAlign;
    public const int ArrayElemAlign = Core.UbmtTables.ArrayElemAlign;
    public const int StructAlign = Core.UbmtTables.StructAlign;
}
