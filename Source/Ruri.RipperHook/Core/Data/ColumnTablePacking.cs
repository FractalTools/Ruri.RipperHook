using System.Runtime.InteropServices;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public static class ColumnTablePacking
{
    public const string TextKind = "text";
    public const string IntegerKind = "int";
    public const string RealKind = "real";
    public const string BlobKind = "blob";

    public static (string Name, int RowCount, string[] Columns, string[] Kinds, byte[][] Blobs, byte[][] Offsets)
        Pack(ColumnTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string[] names = new string[table.Columns.Length];
        string[] kinds = new string[table.Columns.Length];
        byte[][] blobs = new byte[table.Columns.Length][];
        byte[][] offsets = new byte[table.Columns.Length][];
        for (int index = 0; index < table.Columns.Length; index++)
        {
            Column column = table.Columns[index];
            names[index] = column.Name;
            switch (column)
            {
                case Utf8Column text:
                    kinds[index] = TextKind;
                    blobs[index] = text.Blob;
                    offsets[index] = MemoryMarshal.AsBytes(text.Offsets.AsSpan()).ToArray();
                    break;
                case BlobColumn payload:
                    kinds[index] = BlobKind;
                    blobs[index] = payload.Payload;
                    offsets[index] = MemoryMarshal.AsBytes(payload.Offsets.AsSpan()).ToArray();
                    break;
                case IntegerColumn integers:
                    kinds[index] = IntegerKind;
                    blobs[index] = MemoryMarshal.AsBytes(integers.Values.AsSpan()).ToArray();
                    offsets[index] = [];
                    break;
                case RealColumn reals:
                    kinds[index] = RealKind;
                    blobs[index] = MemoryMarshal.AsBytes(reals.Values.AsSpan()).ToArray();
                    offsets[index] = [];
                    break;
                default:
                    throw new InvalidOperationException(
                        $"column '{column.Name}' has unsupported shape {column.GetType().Name}");
            }
        }
        return (table.Name, table.RowCount, names, kinds, blobs, offsets);
    }
}
