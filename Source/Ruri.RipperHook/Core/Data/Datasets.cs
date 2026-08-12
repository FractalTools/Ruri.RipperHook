using System.Collections.Concurrent;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

public static class Datasets
{
    public delegate ColumnTable TableProducer(DataRequest request);

    public delegate byte[] BlobProducer(DataRequest request);

    public sealed record Dataset(
        string Id,
        string[] Parameters,
        string Description,
        TableProducer? Table,
        BlobProducer? Blob);

    private static readonly ConcurrentDictionary<string, Dataset> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ColumnTable> Cache = new(StringComparer.Ordinal);

    public static void Register(string id, string[] parameters, string description, TableProducer producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(producer);
        Registered[id] = new Dataset(id, parameters ?? [], description ?? string.Empty, producer, null);
    }

    public static void RegisterBlob(string id, string[] parameters, string description, BlobProducer producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(producer);
        Registered[id] = new Dataset(id, parameters ?? [], description ?? string.Empty, null, producer);
    }

    public static void Clear(string idPrefix)
    {
        foreach (string id in Registered.Keys.Where(key => key.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Registered.TryRemove(id, out _);
        }
        ClearCache();
    }

    public static void ClearCache() => Cache.Clear();

    public static Dataset[] Available() =>
        Registered.Values.OrderBy(dataset => dataset.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public static (string Handle, ColumnTable Table) Table(string id, string[] args, CancellationToken cancellation = default, CabTable? map = null)
    {
        Dataset dataset = Resolve(id, args);
        if (dataset.Table is null)
        {
            throw new InvalidOperationException($"dataset '{id}' is a blob, not a table -- ask for it with Blob().");
        }
        string handle = HandleOf(id, args);
        if (Cache.TryGetValue(handle, out ColumnTable? cached))
        {
            return (handle, cached);
        }
        ColumnTable table = dataset.Table(new DataRequest(dataset, args, cancellation, map));
        Cache[handle] = table;
        TableRegistry.Register(handle, table);
        return (handle, table);
    }

    public static byte[] Blob(string id, string[] args, CancellationToken cancellation = default, CabTable? map = null)
    {
        Dataset dataset = Resolve(id, args);
        if (dataset.Blob is null)
        {
            throw new InvalidOperationException($"dataset '{id}' is a table, not a blob -- ask for it with Table().");
        }
        return dataset.Blob(new DataRequest(dataset, args, cancellation, map));
    }

    private static Dataset Resolve(string id, string[] args)
    {
        if (!Registered.TryGetValue(id, out Dataset? dataset))
        {
            string known = string.Join(", ", Available().Select(entry => entry.Id));
            throw new InvalidOperationException(
                $"no dataset '{id}' is published. The active game publishes: {(known.Length == 0 ? "(none -- is a game hook enabled?)" : known)}");
        }
        int wanted = dataset.Parameters.Length;
        if ((args?.Length ?? 0) < wanted)
        {
            throw new ArgumentException(
                $"dataset '{id}' needs {wanted} argument(s) ({string.Join(", ", dataset.Parameters)}); got {args?.Length ?? 0}.");
        }
        return dataset;
    }

    private const string ArgumentSeparator = "\u001f";

    private static string HandleOf(string id, string[] args) =>
        args is { Length: > 0 }
            ? id + ArgumentSeparator + string.Join(ArgumentSeparator, args)
            : id;
}
