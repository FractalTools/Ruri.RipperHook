using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public readonly struct DataRequest
{
    private readonly Datasets.Dataset _dataset;
    private readonly CabTable? _map;

    public string[] Args { get; }
    public CancellationToken Cancellation { get; }

    internal DataRequest(Datasets.Dataset dataset, string[] args, CancellationToken cancellation, CabTable? map)
    {
        _dataset = dataset;
        Args = args ?? [];
        Cancellation = cancellation;
        _map = map;
    }

    public CabTable Map => _map ?? throw new InvalidOperationException(
        $"dataset '{_dataset.Id}' reads from bundles and needs a loaded cabmap.");

    public bool HasMap => _map is not null;

    public string Text(int index)
    {
        if (index < 0 || index >= Args.Length)
        {
            string name = index >= 0 && index < _dataset.Parameters.Length
                ? _dataset.Parameters[index]
                : $"#{index}";
            throw new ArgumentException($"dataset '{_dataset.Id}' needs argument '{name}'.");
        }
        return Args[index];
    }

    public int Integer(int index) =>
        int.TryParse(Text(index), out int value)
            ? value
            : throw new ArgumentException(
                $"dataset '{_dataset.Id}' argument '{Name(index)}' must be a whole number; got '{Text(index)}'.");

    public bool Flag(int index) => Text(index) is "1" or "true" or "True";

    public double Real(int index) =>
        double.TryParse(Text(index), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException(
                $"dataset '{_dataset.Id}' argument '{Name(index)}' must be a number; got '{Text(index)}'.");

    public string[] Rest(int from) => from >= Args.Length ? [] : Args[from..];

    private string Name(int index) =>
        index < _dataset.Parameters.Length ? _dataset.Parameters[index] : $"#{index}";
}

public sealed class TableBuilder
{
    private readonly string _name;
    private readonly string[] _columns;
    private readonly bool[] _numeric;
    private readonly Utf8ColumnBuilder?[] _text;
    private readonly List<double>[] _numbers;
    private int _cursor;
    private int _rows;

    public TableBuilder(string name, params string[] columns)
    {
        _name = name;
        _columns = columns.Select(column => column.TrimEnd('#')).ToArray();
        _numeric = columns.Select(column => column.EndsWith('#')).ToArray();
        _text = new Utf8ColumnBuilder?[columns.Length];
        _numbers = new List<double>[columns.Length];
        for (int index = 0; index < columns.Length; index++)
        {
            if (_numeric[index])
            {
                _numbers[index] = [];
            }
            else
            {
                _text[index] = new Utf8ColumnBuilder(0);
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

    public TableBuilder Add(bool value) => _numeric[_cursor] ? Add(value ? 1d : 0d) : Add(value ? "1" : "0");

    public TableBuilder Row(params object?[] values)
    {
        foreach (object? value in values)
        {
            switch (value)
            {
                case null: Add(string.Empty); break;
                case string text: Add(text); break;
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
            columns[index] = _numeric[index]
                ? new RealColumn { Name = _columns[index], Values = _numbers[index].ToArray() }
                : _text[index]!.Build(_columns[index]);
        }
        return new ColumnTable { Name = _name, RowCount = _rows, Columns = columns };
    }
}
