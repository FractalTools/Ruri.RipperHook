using System.Globalization;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.Data;

public readonly struct DataRequest
{
    private readonly Datasets.Dataset _dataset;
    private readonly Dictionary<string, string[]> _values;
    private readonly CabTable? _map;

    public CancellationToken Cancellation { get; }

    internal DataRequest(Datasets.Dataset dataset, Dictionary<string, string[]> values,
        CancellationToken cancellation, CabTable? map)
    {
        _dataset = dataset;
        _values = values;
        Cancellation = cancellation;
        _map = map;
    }

    public CabTable Map => _map ?? throw new InvalidOperationException(
        $"dataset '{_dataset.Id}' reads from bundles and needs a loaded cabmap.");

    public bool HasMap => _map is not null;

    public string GameRoot => Session.GameRoot;

    public string[] Roots => Session.RootsOrThrow(_dataset.Id);

    public string Text(string name)
    {
        string[] values = Values(name, ParamKind.Text);
        return values.Length == 0 ? string.Empty : values[0];
    }

    public int Integer(string name)
    {
        string[] values = Values(name, ParamKind.Integer);
        if (values.Length == 0)
        {
            return 0;
        }
        return int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException(
                $"dataset '{_dataset.Id}' argument '{name}' must be a whole number; got '{values[0]}'.");
    }

    public double Real(string name)
    {
        string[] values = Values(name, ParamKind.Real);
        if (values.Length == 0)
        {
            return 0d;
        }
        return double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new ArgumentException(
                $"dataset '{_dataset.Id}' argument '{name}' must be a number; got '{values[0]}'.");
    }

    public bool Flag(string name)
    {
        string[] values = Values(name, ParamKind.Flag);
        return values.Length != 0 && values[0] is "1" or "true" or "True";
    }

    public string[] List(string name) => Values(name, ParamKind.TextList);

    public int[] Integers(string name)
    {
        string[] values = List(name);
        int[] parsed = new int[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            parsed[index] = int.TryParse(values[index], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int value)
                ? value
                : throw new ArgumentException(
                    $"dataset '{_dataset.Id}' argument '{name}' takes whole numbers; got '{values[index]}'.");
        }
        return parsed;
    }

    private string[] Values(string name, ParamKind kind)
    {
        DataParam? declared = _dataset.Parameters.FirstOrDefault(
            parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            throw new InvalidOperationException(
                $"dataset '{_dataset.Id}' reads an argument '{name}' it never declared; it declares: "
                + $"{string.Join(", ", _dataset.Parameters.Select(parameter => parameter.ToString()))}.");
        }
        if (declared.Kind != kind)
        {
            throw new InvalidOperationException(
                $"dataset '{_dataset.Id}' declares '{name}' as {declared.Kind} but reads it as {kind}.");
        }
        return _values.TryGetValue(name, out string[]? values) ? values : [];
    }
}
