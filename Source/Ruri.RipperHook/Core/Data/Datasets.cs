using System.Collections.Concurrent;
using System.Text;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public static class Datasets
{
    public delegate ColumnTable TableProducer(DataRequest request);

    public delegate byte[] BlobProducer(DataRequest request);

    public sealed record Dataset(
        string Id,
        DataRole Role,
        DataParam[] Parameters,
        string Description,
        TableProducer? Table,
        BlobProducer? Blob);

    private static readonly ConcurrentDictionary<string, Dataset> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ColumnTable> Cache = new(StringComparer.Ordinal);

    public static void Publish(string id, DataRole role, DataParam[] parameters, string description,
        TableProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        Registered[Declared(id, parameters)] = new Dataset(id, role, parameters, description ?? string.Empty,
            producer, null);
        ClearCache();
    }

    public static void PublishBlob(string id, DataRole role, DataParam[] parameters, string description,
        BlobProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        Registered[Declared(id, parameters)] = new Dataset(id, role, parameters, description ?? string.Empty,
            null, producer);
        ClearCache();
    }

    private static string Declared(string id, DataParam[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(parameters);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataParam parameter in parameters)
        {
            if (!seen.Add(parameter.Name))
            {
                throw new ArgumentException($"dataset '{id}' declares '{parameter.Name}' twice.");
            }
        }
        return id;
    }

    public static void Clear(string idPrefix)
    {
        foreach (string id in Registered.Keys
                     .Where(key => key.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Registered.TryRemove(id, out _);
        }
        ClearCache();
    }

    public static void ClearCache() => Cache.Clear();

    public static Dataset[] Available() =>
        Registered.Values.OrderBy(dataset => dataset.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public static Dataset[] WithRole(DataRole role) =>
        Available().Where(dataset => dataset.Role == role).ToArray();

    public static (string Handle, ColumnTable Table) Table(string id, string[] namedArgs,
        CancellationToken cancellation = default, CabTable? map = null)
    {
        (Dataset dataset, Dictionary<string, string[]> values) = Resolve(id, namedArgs);
        if (dataset.Table is null)
        {
            throw new InvalidOperationException($"dataset '{id}' is a blob, not a table -- ask for it with Blob().");
        }
        string handle = HandleOf(id, values);
        if (Cache.TryGetValue(handle, out ColumnTable? cached))
        {
            return (handle, cached);
        }
        ColumnTable table = dataset.Table(new DataRequest(dataset, values, cancellation, map));
        Cache[handle] = table;
        TableRegistry.Register(handle, table);
        return (handle, table);
    }

    public static byte[] Blob(string id, string[] namedArgs, CancellationToken cancellation = default,
        CabTable? map = null)
    {
        (Dataset dataset, Dictionary<string, string[]> values) = Resolve(id, namedArgs);
        if (dataset.Blob is null)
        {
            throw new InvalidOperationException($"dataset '{id}' is a table, not a blob -- ask for it with Blob().");
        }
        return dataset.Blob(new DataRequest(dataset, values, cancellation, map));
    }

    private static (Dataset Dataset, Dictionary<string, string[]> Values) Resolve(string id, string[] namedArgs)
    {
        if (!Registered.TryGetValue(id, out Dataset? dataset))
        {
            string known = string.Join(", ", Available().Select(entry => entry.Id));
            throw new InvalidOperationException(
                $"no dataset '{id}' is published. The active game publishes: "
                + $"{(known.Length == 0 ? "(none -- is a game hook enabled?)" : known)}");
        }
        return (dataset, Bind(dataset, namedArgs ?? []));
    }

    private static Dictionary<string, string[]> Bind(Dataset dataset, string[] namedArgs)
    {
        if (namedArgs.Length % 2 != 0)
        {
            throw new ArgumentException(
                $"dataset '{dataset.Id}' takes name/value pairs; got {namedArgs.Length} argument string(s).");
        }
        Dictionary<string, List<string>> collected = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < namedArgs.Length; index += 2)
        {
            string name = namedArgs[index];
            DataParam declared = dataset.Parameters.FirstOrDefault(
                                     parameter => string.Equals(parameter.Name, name,
                                         StringComparison.OrdinalIgnoreCase))
                                 ?? throw new ArgumentException(
                                     $"dataset '{dataset.Id}' has no argument '{name}'; it takes: "
                                     + $"{Signature(dataset)}.");
            if (!collected.TryGetValue(declared.Name, out List<string>? values))
            {
                values = [];
                collected[declared.Name] = values;
            }
            else if (!declared.Repeatable)
            {
                throw new ArgumentException(
                    $"dataset '{dataset.Id}' argument '{declared.Name}' is given more than once but is not a list.");
            }
            values.Add(namedArgs[index + 1]);
        }
        foreach (DataParam parameter in dataset.Parameters)
        {
            if (parameter.Required && !collected.ContainsKey(parameter.Name))
            {
                throw new ArgumentException(
                    $"dataset '{dataset.Id}' needs argument '{parameter.Name}'; it takes: {Signature(dataset)}.");
            }
        }
        Dictionary<string, string[]> bound = new(collected.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, List<string> values) in collected)
        {
            bound[name] = values.ToArray();
        }
        return bound;
    }

    public static string Signature(Dataset dataset) =>
        dataset.Parameters.Length == 0
            ? "(no arguments)"
            : string.Join(", ", dataset.Parameters.Select(parameter => parameter.ToString()));

    private static string HandleOf(string id, Dictionary<string, string[]> values)
    {
        StringBuilder handle = new(id);
        foreach ((string name, string[] entries) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(handle, name);
            handle.Append('=').Append(entries.Length);
            foreach (string entry in entries)
            {
                Append(handle, entry);
            }
        }
        return handle.ToString();
    }

    private static void Append(StringBuilder handle, string part) =>
        handle.Append('|').Append(part.Length).Append(':').Append(part);
}
