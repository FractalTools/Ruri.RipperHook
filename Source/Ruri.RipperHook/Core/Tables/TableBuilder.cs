namespace Ruri.RipperHook.Tables;

public sealed class TableBuilder
{
    private const char NumericMark = '#';
    private const char BlobMark = '@';

    private readonly string _name;
    private readonly string[] _columns;
    private readonly ColumnShape[] _shapes;
    private readonly Utf8ColumnBuilder?[] _text;
    private readonly BlobColumnBuilder?[] _blobs;
    private readonly List<double>[] _numbers;
    private int _cursor;
    private int _rows;

    private enum ColumnShape
    {
        Text,
        Real,
        Blob,
    }

    public TableBuilder(string name, params string[] columns)
    {
        _name = name;
        _columns = columns.Select(column => column.TrimEnd(NumericMark, BlobMark)).ToArray();
        _shapes = columns.Select(column =>
            column.EndsWith(NumericMark) ? ColumnShape.Real :
            column.EndsWith(BlobMark) ? ColumnShape.Blob : ColumnShape.Text).ToArray();
        _text = new Utf8ColumnBuilder?[columns.Length];
        _blobs = new BlobColumnBuilder?[columns.Length];
        _numbers = new List<double>[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            switch (_shapes[index])
            {
                case ColumnShape.Real:
                    _numbers[index] = [];
                    break;
                case ColumnShape.Blob:
                    _blobs[index] = new BlobColumnBuilder(0);
                    break;
                default:
                    _text[index] = new Utf8ColumnBuilder(0);
                    break;
            }
        }
    }

    public int RowCount => _rows;

    public TableBuilder Add(string? value)
    {
        Slot(out int index);
        _text[index]!.Add(value ?? string.Empty);
        return this;
    }

    public TableBuilder Add(double value)
    {
        Slot(out int index);
        _numbers[index].Add(value);
        return this;
    }

    public TableBuilder Add(long value) => Add((double)value);

    public TableBuilder Add(ReadOnlySpan<byte> value)
    {
        Slot(out int index);
        _blobs[index]!.Add(value);
        return this;
    }

    public TableBuilder Add(bool value) => _shapes[_cursor] == ColumnShape.Real ? Add(value ? 1d : 0d) : Add(value ? "1" : "0");

    public TableBuilder Row(params object?[] values)
    {
        foreach (object? value in values)
        {
            switch (value)
            {
                case null when _shapes[_cursor] == ColumnShape.Blob: Add(ReadOnlySpan<byte>.Empty); break;
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
            columns[index] = _shapes[index] switch
            {
                ColumnShape.Real => new RealColumn { Name = _columns[index], Values = _numbers[index].ToArray() },
                ColumnShape.Blob => _blobs[index]!.Build(_columns[index]),
                _ => _text[index]!.Build(_columns[index]),
            };
        }
        return new ColumnTable { Name = _name, RowCount = _rows, Columns = columns };
    }
}
