namespace Ruri.UEShaderTpkDumper.Parser;

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
        ["FVector"]       = new(12, 16, "FLOAT32", "Float3",   3, 1, false),
        ["FVector4"]      = new(16, 16, "FLOAT32", "Float4",   4, 1, false),
        ["FMatrix"]       = new(64, 16, "FLOAT32", "Float4x4", 4, 4, true),
        ["FMatrix3x4"]    = new(48, 16, "FLOAT32", "Float3x4", 3, 4, true),
        ["FMatrix44"]     = new(64, 16, "FLOAT32", "Float4x4", 4, 4, true),
    };

    public static readonly IReadOnlyDictionary<string, string> ScalarArrayPack = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["uint32"] = "FUintVector4",
        ["uint"]   = "FUintVector4",
        ["int32"]  = "FIntVector4",
        ["int"]    = "FIntVector4",
        ["float"]  = "FVector4f",
    };

    public const int ResourceSize = UbmtTablesAlignment.PointerAlign;
    public const int ResourceAlign = UbmtTablesAlignment.PointerAlign;
}

internal static class UbmtTablesAlignment
{
    public const int PointerAlign = Core.UbmtTables.PointerAlign;
    public const int ArrayElemAlign = Core.UbmtTables.ArrayElemAlign;
    public const int StructAlign = Core.UbmtTables.StructAlign;
}
