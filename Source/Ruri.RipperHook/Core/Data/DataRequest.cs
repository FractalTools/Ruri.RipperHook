using System.Globalization;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.Data;

public readonly struct DataRequest
{
    private readonly Datasets.Dataset _dataset;
    private readonly Dictionary<string, string[]> _values;
    private readonly CabTable? _map;

    public CancellationToken Cancellation { get; }

    /// <summary>What the CALLER states and the install cannot: a baked performance, a rig's own
    /// rest. Empty for every dataset that is purely a question about the install.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    internal DataRequest(Datasets.Dataset dataset, Dictionary<string, string[]> values,
        CancellationToken cancellation, CabTable? map, ReadOnlyMemory<byte> payload = default)
    {
        _dataset = dataset;
        _values = values;
        Cancellation = cancellation;
        _map = map;
        Payload = payload;
    }

    public CabTable Map => _map ?? throw new InvalidOperationException(
        $"dataset '{_dataset.Id}' reads from bundles and needs a loaded cabmap.");

    public bool HasMap => _map is not null;

    public string GameRoot => Session.GameRoot;

    public string[] Roots => Session.RootsOrThrow(_dataset.Id);

    /// <summary>Whether the caller stated the argument at all, whatever its kind: how an optional argument's absence is told from its default.</summary>
    public bool Given(string name)
    {
        Declared(name);
        return _values.TryGetValue(name, out string[]? values) && values.Length > 0;
    }

    public string Text(string name)
    {
        string[] values = Values(name, ParamKind.Text);
        return values.Length == 0 ? string.Empty : values[0];
    }

    /// <summary>The game language this request reads text in: the one it stated, else the one
    /// the host's own display language reads as. The ONE place "unstated" is answered -- a
    /// dataset that answered it itself would be a second rule, and the two drift silently
    /// (measured: one scene dataset returned NO display names at all when unstated).</summary>
    public string Language(string name)
    {
        string[] values = Values(name, ParamKind.Language);
        return values.Length > 0 && values[0].Length > 0 ? values[0] : Session.Language;
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

    private DataParam Declared(string name)
    {
        return _dataset.Parameters.FirstOrDefault(
            parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"dataset '{_dataset.Id}' reads an argument '{name}' it never declared; it declares: "
                + $"{string.Join(", ", _dataset.Parameters.Select(parameter => parameter.ToString()))}.");
    }

    private string[] Values(string name, ParamKind kind)
    {
        DataParam declared = Declared(name);
        if (declared.Kind != kind)
        {
            throw new InvalidOperationException(
                $"dataset '{_dataset.Id}' declares '{name}' as {declared.Kind} but reads it as {kind}.");
        }
        return _values.TryGetValue(name, out string[]? values) ? values : [];
    }
}
