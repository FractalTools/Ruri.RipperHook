using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Ruri.RipperHook.Tables;

public enum ColumnKind
{
    Text,
    Blob,
    Integer,
    Real,
}

public sealed class Column
{
    public const int ScalarWidth = 8;

    public required string Name { get; init; }
    public required ColumnKind Kind { get; init; }
    public required byte[] Data { get; init; }
    public required int[] Offsets { get; init; }
    public ColumnRole Role { get; init; }

    public bool Sliced => Kind is ColumnKind.Text or ColumnKind.Blob;

    public int RowCount => Sliced ? Math.Max(Offsets.Length - 1, 0) : Data.Length / ScalarWidth;

    public ReadOnlySpan<byte> Bytes(int row) => Sliced
        ? Data.AsSpan(Offsets[row], Offsets[row + 1] - Offsets[row])
        : Data.AsSpan(row * ScalarWidth, ScalarWidth);

    public ReadOnlySpan<long> Integers => MemoryMarshal.Cast<byte, long>(Data);

    public ReadOnlySpan<double> Reals => MemoryMarshal.Cast<byte, double>(Data);

    public string Text(int row) => Kind switch
    {
        ColumnKind.Text => Encoding.UTF8.GetString(Bytes(row)),
        ColumnKind.Integer => Integers[row].ToString(CultureInfo.InvariantCulture),
        ColumnKind.Real => Reals[row].ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    public double Real(int row) => Kind switch
    {
        ColumnKind.Real => Reals[row],
        ColumnKind.Integer => Integers[row],
        ColumnKind.Text => double.TryParse(Text(row), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double parsed) ? parsed : 0d,
        _ => 0d,
    };

    public long Integer(int row) => Kind == ColumnKind.Integer ? Integers[row] : (long)Real(row);

    public bool Truthy(int row) => Kind switch
    {
        ColumnKind.Text => !Bytes(row).IsEmpty && !Bytes(row).SequenceEqual("0"u8),
        ColumnKind.Blob => !Bytes(row).IsEmpty,
        _ => Real(row) != 0d,
    };

    public Column Restated(string name, ColumnRole role) =>
        new() { Name = name, Kind = Kind, Data = Data, Offsets = Offsets, Role = role };
}

public sealed class ColumnTable
{
    public required string Name { get; init; }
    public required int RowCount { get; init; }
    public required Column[] Columns { get; init; }

    public Column this[string name] => Find(name)
        ?? throw new KeyNotFoundException($"table '{Name}' has no column '{name}'");

    public Column? Find(string name)
    {
        foreach (Column column in Columns)
        {
            if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }
        return null;
    }

    public Column[] WithRole(ColumnRole role) =>
        Columns.Where(column => (column.Role & role) == role).ToArray();

    public Column? FirstWithRole(ColumnRole role) =>
        Columns.FirstOrDefault(column => (column.Role & role) == role);

    public ColumnTable SelectRows(int[] rows) => new()
    {
        Name = Name,
        RowCount = rows.Length,
        Columns = Columns.Select(column => Take(column, rows)).ToArray(),
    };

    private static Column Take(Column column, int[] rows)
    {
        ColumnBuilder builder = new(column.Kind, rows.Length);
        foreach (int row in rows)
        {
            builder.Add(column.Bytes(row));
        }
        return builder.Build(column.Name, column.Role);
    }

    public ColumnTable DistinctBy(string distinctColumn, string preferColumn)
    {
        Column key = this[distinctColumn];
        Column? prefer = preferColumn.Length == 0 ? null : this[preferColumn];
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
            if (prefer is not null && prefer.Bytes(order[existing]).IsEmpty && !prefer.Bytes(row).IsEmpty)
            {
                order[existing] = row;
            }
        }
        return SelectRows(order.ToArray());
    }
}

public sealed class ColumnBuilder
{
    private readonly ColumnKind _kind;
    private byte[] _data;
    private int _length;
    private int[] _offsets;
    private int _rows;

    public ColumnBuilder(ColumnKind kind, int rowCount, int expectedBytesPerRow = 24)
    {
        _kind = kind;
        int width = kind is ColumnKind.Text or ColumnKind.Blob ? expectedBytesPerRow : Column.ScalarWidth;
        _data = new byte[Math.Max(64, rowCount * width)];
        _offsets = kind is ColumnKind.Text or ColumnKind.Blob ? new int[Math.Max(1, rowCount) + 1] : [];
    }

    public ColumnKind Kind => _kind;

    public int RowCount => _rows;

    public void Add(ReadOnlySpan<byte> bytes)
    {
        if (_length + bytes.Length > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(_data.Length * 2, _length + bytes.Length));
        }
        bytes.CopyTo(_data.AsSpan(_length));
        _length += bytes.Length;
        _rows++;
        if (_offsets.Length == 0)
        {
            return;
        }
        if (_rows >= _offsets.Length)
        {
            Array.Resize(ref _offsets, _offsets.Length * 2);
        }
        _offsets[_rows] = _length;
    }

    public void Add(string? text) => Add(text is null or "" ? [] : Encoding.UTF8.GetBytes(text));

    public void Add(long value)
    {
        if (_kind == ColumnKind.Real)
        {
            Add((double)value);
            return;
        }
        Add(_kind == ColumnKind.Text
            ? Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture))
            : MemoryMarshal.AsBytes(new ReadOnlySpan<long>(in value)));
    }

    public void Add(double value)
    {
        if (_kind == ColumnKind.Integer)
        {
            Add((long)value);
            return;
        }
        Add(_kind == ColumnKind.Text
            ? Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture))
            : MemoryMarshal.AsBytes(new ReadOnlySpan<double>(in value)));
    }

    public void AddBlank()
    {
        if (_kind is ColumnKind.Text or ColumnKind.Blob)
        {
            Add(ReadOnlySpan<byte>.Empty);
            return;
        }
        Span<byte> zero = stackalloc byte[Column.ScalarWidth];
        Add(zero);
    }

    public Column Build(string name, ColumnRole role = ColumnRole.None)
    {
        byte[] data = new byte[_length];
        Array.Copy(_data, data, _length);
        int[] offsets = [];
        if (_offsets.Length != 0)
        {
            offsets = new int[_rows + 1];
            Array.Copy(_offsets, offsets, _rows + 1);
        }
        return new Column { Name = name, Kind = _kind, Data = data, Offsets = offsets, Role = role };
    }
}
