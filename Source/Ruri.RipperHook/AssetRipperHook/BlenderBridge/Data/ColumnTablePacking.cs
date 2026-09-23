using System.Runtime.InteropServices;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

public static class ColumnTablePacking
{
    public static readonly string[] KindNames = ["text", "blob", "int", "real"];

    public static string KindName(ColumnKind kind) => KindNames[(int)kind];

    public static PinnedTable Pin(string handle, ColumnTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new PinnedTable(handle, table);
    }
}

public sealed class PinnedTable : IDisposable
{
    private GCHandle[] _handles;

    internal PinnedTable(string handle, ColumnTable table)
    {
        Handle = handle;
        Name = table.Name;
        RowCount = table.RowCount;
        int count = table.Columns.Length;
        Names = new string[count];
        Titles = new string[count];
        Kinds = new string[count];
        Roles = new int[count];
        Addresses = new long[count * 2];
        Lengths = new int[count * 2];
        _handles = new GCHandle[count * 2];
        for (int index = 0; index < count; index++)
        {
            Column column = table.Columns[index];
            Names[index] = column.Name;
            Titles[index] = column.Display;
            Kinds[index] = ColumnTablePacking.KindName(column.Kind);
            Roles[index] = (int)column.Role;
            Hold(index * 2, column.Data, column.Data.Length);
            Hold(index * 2 + 1, column.Offsets, column.Offsets.Length * sizeof(int));
        }
    }

    public string Handle { get; }

    public string Name { get; }

    public int RowCount { get; }

    public string[] Names { get; }

    public string[] Titles { get; }

    public string[] Kinds { get; }

    public int[] Roles { get; }

    public long[] Addresses { get; }

    public int[] Lengths { get; }

    private void Hold(int slot, Array buffer, int byteLength)
    {
        if (byteLength == 0)
        {
            return;
        }
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        _handles[slot] = pin;
        Addresses[slot] = pin.AddrOfPinnedObject().ToInt64();
        Lengths[slot] = byteLength;
    }

    ~PinnedTable() => Release();

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        GCHandle[] held = Interlocked.Exchange(ref _handles, []);
        for (int slot = 0; slot < held.Length; slot++)
        {
            Addresses[slot] = 0;
            Lengths[slot] = 0;
            if (held[slot].IsAllocated)
            {
                held[slot].Free();
            }
        }
    }
}
