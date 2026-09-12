namespace Ruri.RipperHook.Tables;

public sealed class TableBuilder
{
    private const char NumericMark = '#';
    private const char BlobMark = '@';

    private readonly string _name;
    private readonly string[] _columns;
    private readonly ColumnBuilder[] _builders;
    private readonly ColumnRole[] _roles;
    private int _cursor;
    private int _rows;

    public TableBuilder(string name, params string[] columns)
    {
        _name = name;
        _columns = columns.Select(column => column.TrimEnd(NumericMark, BlobMark)).ToArray();
        _builders = columns.Select(column => new ColumnBuilder(
            column.EndsWith(NumericMark) ? ColumnKind.Real :
            column.EndsWith(BlobMark) ? ColumnKind.Blob : ColumnKind.Text, 0)).ToArray();
        _roles = new ColumnRole[columns.Length];
    }

    public int RowCount => _rows;

    public TableBuilder Role(ColumnRole role, params string[] columns)
    {
        foreach (string column in columns)
        {
            int index = Array.FindIndex(_columns,
                name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new ArgumentException(
                    $"table '{_name}' has no column '{column}' to state as {role}; it has: "
                    + string.Join(", ", _columns));
            }
            _roles[index] |= role;
        }
        return this;
    }

    public TableBuilder Add(string? value)
    {
        Slot(out int index);
        _builders[index].Add(value);
        return this;
    }

    public TableBuilder Add(double value)
    {
        Slot(out int index);
        _builders[index].Add(value);
        return this;
    }

    public TableBuilder Add(long value) => Add((double)value);

    public TableBuilder Add(ReadOnlySpan<byte> value)
    {
        Slot(out int index);
        _builders[index].Add(value);
        return this;
    }

    public TableBuilder Add(bool value) =>
        _builders[_cursor].Kind == ColumnKind.Real ? Add(value ? 1d : 0d) : Add(value ? "1" : "0");

    public TableBuilder Row(params object?[] values)
    {
        foreach (object? value in values)
        {
            switch (value)
            {
                case null when _builders[_cursor].Kind == ColumnKind.Blob: Add(ReadOnlySpan<byte>.Empty); break;
                case null: Add(string.Empty); break;
                case string text: Add(text); break;
                case byte[] bytes: Add(bytes.AsSpan()); break;
                case bool flag: Add(flag); break;
                case double real: Add(real); break;
                case float real: Add(real); break;
                case long integer: Add(integer); break;
                case int integer: Add(integer); break;
                default: Add(value.ToString()); break;
            }
        }
        return this;
    }

    private void Slot(out int index)
    {
        index = _cursor;
        _cursor++;
        if (_cursor != _columns.Length)
        {
            return;
        }
        _cursor = 0;
        _rows++;
    }

    public ColumnTable Build()
    {
        if (_cursor != 0)
        {
            throw new InvalidOperationException(
                $"table '{_name}' has a half-written row: {_cursor} of {_columns.Length} column(s) pushed.");
        }
        Column[] columns = new Column[_columns.Length];
        for (int index = 0; index < _columns.Length; index++)
        {
            columns[index] = _builders[index].Build(_columns[index], _roles[index]);
        }
        return new ColumnTable { Name = _name, RowCount = _rows, Columns = columns };
    }
}
