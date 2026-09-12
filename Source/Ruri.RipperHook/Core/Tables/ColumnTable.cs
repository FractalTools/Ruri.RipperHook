using System.Text;

namespace Ruri.RipperHook.Tables;

public abstract class Column
{
    public required string Name { get; init; }
    public abstract int RowCount { get; }
}

public sealed class Utf8Column : Column
{
    public required byte[] Blob { get; init; }
    public required int[] Offsets { get; init; }

    public override int RowCount => Offsets.Length - 1;

    public ReadOnlySpan<byte> Utf8(int row) => Blob.AsSpan(Offsets[row], Offsets[row + 1] - Offsets[row]);

    public string Text(int row) => Encoding.UTF8.GetString(Blob, Offsets[row], Offsets[row + 1] - Offsets[row]);
}

public sealed class BlobColumn : Column
{
    public required byte[] Payload { get; init; }
    public required int[] Offsets { get; init; }

    public override int RowCount => Offsets.Length - 1;

    public ReadOnlySpan<byte> Bytes(int row) => Payload.AsSpan(Offsets[row], Offsets[row + 1] - Offsets[row]);
}

public sealed class IntegerColumn : Column
{
    public required long[] Values { get; init; }
    public override int RowCount => Values.Length;
}

public sealed class RealColumn : Column
{
    public required double[] Values { get; init; }
    public override int RowCount => Values.Length;
}

public sealed class ColumnTable
{
    public required string Name { get; init; }
    public required int RowCount { get; init; }
    public required Column[] Columns { get; init; }

    public ColumnTable SelectRows(int[] rows)
    {
        Column[] columns = new Column[Columns.Length];
        for (int i = 0; i < Columns.Length; i++)
        {
            columns[i] = Columns[i] switch
            {
                Utf8Column text => Take(text, rows),
                IntegerColumn integers => new IntegerColumn
                {
                    Name = integers.Name,
                    Values = rows.Select(row => integers.Values[row]).ToArray(),
                },
                RealColumn reals => new RealColumn
                {
                    Name = reals.Name,
                    Values = rows.Select(row => reals.Values[row]).ToArray(),
                },
                BlobColumn payload => Take(payload, rows),
                Column other => throw new InvalidOperationException($"cannot subset {other.GetType().Name}"),
            };
        }
        return new ColumnTable { Name = Name, RowCount = rows.Length, Columns = columns };
    }

    private static Utf8Column Take(Utf8Column column, int[] rows)
    {
        Utf8ColumnBuilder builder = new(rows.Length);
        foreach (int row in rows)
        {
            builder.Add(column.Utf8(row));
        }
        return builder.Build(column.Name);
    }

    private static BlobColumn Take(BlobColumn column, int[] rows)
    {
        BlobColumnBuilder builder = new(rows.Length);
        foreach (int row in rows)
        {
            builder.Add(column.Bytes(row));
        }
        return builder.Build(column.Name);
    }

    public ColumnTable DistinctBy(string distinctColumn, string preferColumn)
    {
        Utf8Column key = (Utf8Column)this[distinctColumn];
        Utf8Column? prefer = preferColumn.Length == 0 ? null : (Utf8Column)this[preferColumn];
        Dictionary<string, int> chosen = new(RowCount, StringComparer.Ordinal);
        List<int> order = new(RowCount);
        for (int row = 0; row < RowCount; row++)
        {
            string value = key.Text(row);
            if (value.Length == 0)
            {
                continue;
            }
            if (!chosen.TryGetValue(value, out int existing))
            {
                chosen[value] = order.Count;
                order.Add(row);
                continue;
            }
            if (prefer is not null && prefer.Utf8(order[existing]).IsEmpty && !prefer.Utf8(row).IsEmpty)
            {
                order[existing] = row;
            }
        }
        return SelectRows(order.ToArray());
    }

    public Column this[string name]
    {
        get
        {
            foreach (Column column in Columns)
            {
                if (string.Equals(column.Name, name, StringComparison.Ordinal))
                {
                    return column;
                }
            }
            throw new KeyNotFoundException($"table '{Name}' has no column '{name}'");
        }
    }
}

public sealed class Utf8ColumnBuilder
{
    private byte[] _blob;
    private int _length;
    private int[] _offsets;
    private int _rows;

    public Utf8ColumnBuilder(int rowCount, int expectedBytesPerRow = 24)
    {
        _blob = new byte[Math.Max(16, rowCount * expectedBytesPerRow)];
        _offsets = new int[Math.Max(1, rowCount) + 1];
    }

    public void Add(ReadOnlySpan<byte> utf8)
    {
        if (_length + utf8.Length > _blob.Length)
        {
            Array.Resize(ref _blob, Math.Max(_blob.Length * 2, _length + utf8.Length));
        }
        if (_rows + 1 >= _offsets.Length)
        {
            Array.Resize(ref _offsets, _offsets.Length * 2);
        }
        utf8.CopyTo(_blob.AsSpan(_length));
        _length += utf8.Length;
        _offsets[++_rows] = _length;
    }

    public void Add(string text) => Add(Encoding.UTF8.GetBytes(text));

    public Utf8Column Build(string name)
    {
        byte[] blob = new byte[_length];
        Array.Copy(_blob, blob, _length);
        int[] offsets = _offsets;
        if (offsets.Length != _rows + 1)
        {
            offsets = new int[_rows + 1];
            Array.Copy(_offsets, offsets, _rows + 1);
        }
        return new Utf8Column { Name = name, Blob = blob, Offsets = offsets };
    }
}

public sealed class BlobColumnBuilder
{
    private byte[] _payload;
    private int _length;
    private int[] _offsets;
    private int _rows;

    public BlobColumnBuilder(int rowCount, int expectedBytesPerRow = 256)
    {
        _payload = new byte[Math.Max(64, rowCount * expectedBytesPerRow)];
        _offsets = new int[Math.Max(1, rowCount) + 1];
    }

    public void Add(ReadOnlySpan<byte> bytes)
    {
        if (_length + bytes.Length > _payload.Length)
        {
            Array.Resize(ref _payload, Math.Max(_payload.Length * 2, _length + bytes.Length));
        }
        if (_rows + 1 >= _offsets.Length)
        {
            Array.Resize(ref _offsets, _offsets.Length * 2);
        }
        bytes.CopyTo(_payload.AsSpan(_length));
        _length += bytes.Length;
        _offsets[++_rows] = _length;
    }

    public BlobColumn Build(string name)
    {
        byte[] payload = new byte[_length];
        Array.Copy(_payload, payload, _length);
        int[] offsets = _offsets;
        if (offsets.Length != _rows + 1)
        {
            offsets = new int[_rows + 1];
            Array.Copy(_offsets, offsets, _rows + 1);
        }
        return new BlobColumn { Name = name, Payload = payload, Offsets = offsets };
    }
}
