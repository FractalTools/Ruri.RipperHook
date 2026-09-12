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

    /// <summary>What a person should see this column called. Empty means its own name reads well enough.</summary>
    public string Title { get; init; } = string.Empty;

    public string Display => Title.Length == 0 ? Name : Title;

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
        new() { Name = name, Kind = Kind, Data = Data, Offsets = Offsets, Role = role, Title = Title };
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
        return builder.Build(column.Name, column.Role, column.Title);
    }

    /// <summary>Several tables as ONE, with a column saying which one each row came from.
    ///
    /// What a facet switch is made of. Two projections of the same cast -- the playable
    /// characters and the npcs, the units and their outfits -- are two tables because they
    /// are read differently, but they are ONE list to a person, and a list narrowed by a
    /// switch is a list. Columns are the union of every part's: a row from a part that has
    /// no such column reads blank there, which is the truth about it.</summary>
    public static ColumnTable Stack(string name, string kindColumn, ColumnRole kindRole,
        params (string Kind, ColumnTable Table)[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kindColumn);
        List<Column> shape = [];
        foreach ((_, ColumnTable table) in parts)
        {
            foreach (Column column in table.Columns)
            {
                if (!shape.Exists(seen => string.Equals(seen.Name, column.Name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    shape.Add(column);
                }
            }
        }
        ColumnBuilder kinds = new(ColumnKind.Text, parts.Sum(part => part.Table.RowCount));
        List<ColumnBuilder> builders = shape.Select(column =>
            new ColumnBuilder(column.Kind, parts.Sum(part => part.Table.RowCount))).ToList();
        int rows = 0;
        foreach ((string kind, ColumnTable table) in parts)
        {
            for (int row = 0; row < table.RowCount; row++)
            {
                kinds.Add(kind);
                for (int index = 0; index < shape.Count; index++)
                {
                    Column? found = table.Find(shape[index].Name);
                    if (found is null)
                    {
                        // A part that states nothing for a column contributes nothing to it --
                        // except where the column is the SHIPPED test, whose whole meaning is
                        // "this install has something behind this row". A table carrying no such
                        // column at all has every row shipped (that is what a view does when
                        // nothing carries the role), so a PART carrying none does too: blank
                        // would read as "nothing behind any of these" and hide that part whole.
                        if ((shape[index].Role & ColumnRole.Shipped) != 0)
                        {
                            builders[index].Add(1L);
                        }
                        else
                        {
                            builders[index].AddBlank();
                        }
                    }
                    else if (found.Kind == shape[index].Kind)
                    {
                        builders[index].Add(found.Bytes(row));
                    }
                    else if (shape[index].Sliced)
                    {
                        // Two parts spelling one column differently (a count as text here, a
                        // number there) is the game's own inconsistency, not a reason to write
                        // one part's bytes into the other's shape and read garbage after it.
                        builders[index].Add(found.Text(row));
                    }
                    else
                    {
                        builders[index].Add(found.Real(row));
                    }
                }
                rows++;
            }
        }
        List<Column> built = [kinds.Build(kindColumn, kindRole, "Kind")];
        for (int index = 0; index < shape.Count; index++)
        {
            built.Add(builders[index].Build(shape[index].Name, shape[index].Role, shape[index].Title));
        }
        return new ColumnTable { Name = name, RowCount = rows, Columns = built.ToArray() };
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

    public void Add(string? text)
    {
        if (_kind is ColumnKind.Integer or ColumnKind.Real)
        {
            // A numeric column given text is the flat wire form, not a mistake: parse it
            // rather than writing its bytes, which would make every later row of that
            // column read as garbage.
            Add(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double parsed) ? parsed : 0d);
            return;
        }
        Add(text is null or "" ? [] : Encoding.UTF8.GetBytes(text));
    }

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

    public Column Build(string name, ColumnRole role = ColumnRole.None, string title = "")
    {
        byte[] data = new byte[_length];
        Array.Copy(_data, data, _length);
        int[] offsets = [];
        if (_offsets.Length != 0)
        {
            offsets = new int[_rows + 1];
            Array.Copy(_offsets, offsets, _rows + 1);
        }
        return new Column
        {
            Name = name, Kind = _kind, Data = data, Offsets = offsets, Role = role, Title = title,
        };
    }
}
