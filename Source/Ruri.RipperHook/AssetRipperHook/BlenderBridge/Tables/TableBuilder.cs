namespace Ruri.RipperHook.BlenderBridge.Tables;

public sealed class TableBuilder
{
    private const char NumericMark = '#';
    private const char BlobMark = '@';
    private const char TitleMark = '|';

    private readonly string _name;
    private readonly string[] _columns;
    private readonly string[] _titles;
    private readonly ColumnBuilder[] _builders;
    private readonly ColumnRole[] _roles;
    private int _cursor;
    private int _rows;

    /// <summary>A column is "name", "name#" (a number), "name@" (a payload), and any of those
    /// followed by "|What A Person Sees" where its own name would not read well.</summary>
    public TableBuilder(string name, params string[] columns)
    {
        _name = name;
        string[] stated = columns.Select(column => column.Split(TitleMark)[0]).ToArray();
        _titles = columns.Select(column =>
        {
            int mark = column.IndexOf(TitleMark);
            return mark < 0 ? string.Empty : column[(mark + 1)..];
        }).ToArray();
        _columns = stated.Select(column => column.TrimEnd(NumericMark, BlobMark)).ToArray();
        _builders = stated.Select(column => new ColumnBuilder(
            column.EndsWith(NumericMark) ? ColumnKind.Real :
            column.EndsWith(BlobMark) ? ColumnKind.Blob : ColumnKind.Text, 0)).ToArray();
        _roles = new ColumnRole[columns.Length];
    }

    public int RowCount => _rows;

    /// <summary>Every column's role at once, positionally -- what a table arriving as a flat
    /// wire form states, where naming each column again would just be the same list twice.</summary>
    public TableBuilder Roles(params ColumnRole[] perColumn)
    {
        if (perColumn.Length > _roles.Length)
        {
            throw new ArgumentException(
                $"table '{_name}' has {_roles.Length} column(s) but {perColumn.Length} role(s) were stated.");
        }
        for (int index = 0; index < perColumn.Length; index++)
        {
            _roles[index] |= perColumn[index];
        }
        return this;
    }

    public TableBuilder Role(ColumnRole role, params string[] columns)
    {
        int previous = -1;
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
            // Several columns in one role are a FALLBACK CHAIN, and a chain's order
            // is the table's own column order -- there is no second place stating
            // it. So a chain written in an order the columns are not in is a build
            // error here, rather than a list that quietly shows the wrong thing.
            if (index <= previous)
            {
                throw new ArgumentException(
                    $"table '{_name}' states {role} as {string.Join(" then ", columns)}, but its "
                    + $"columns run {string.Join(", ", _columns)} -- a fallback chain is read in "
                    + "column order, so put the columns in the order the chain wants them.");
            }
            previous = index;
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
            columns[index] = _builders[index].Build(_columns[index], _roles[index], _titles[index]);
        }
        return new ColumnTable { Name = _name, RowCount = _rows, Columns = columns };
    }
}
