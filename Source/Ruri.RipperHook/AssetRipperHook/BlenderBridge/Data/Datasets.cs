using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

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
        BlobProducer? Blob)
    {
        /// <summary>Where this one sits in the order its publisher declared it.
        ///
        /// Reading order is a STATEMENT, not an accident of spelling: a decoder registers the
        /// list a person opens on first and the rest behind it, and a surface that shows the
        /// datasets of one role opens on the first of them. Sorting by id instead made that
        /// alphabetical, which put a story's cast list in front of its animations and a
        /// roster's empty half in front of the one with rows.</summary>
        public long Sequence { get; init; }
        public bool Cache { get; init; } = true;
        public bool ReaderIndependent { get; init; }
        public bool ReaderTransition { get; init; }
        public Func<DataRequest, string>? CacheIdentity { get; init; }
        public object? CacheGate { get; init; }
    }

    /// <summary>
    /// How many bytes of answers already made the session keeps to hand out again (see
    /// <see cref="KeptAnswers"/>). Every roster, language and folder list a host draws fits many
    /// times over; a selection's meshes do not, and are never kept.
    /// </summary>
    public const long KeptAnswerBytes = 64L << 20;

    private static readonly ConcurrentDictionary<string, Dataset> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly KeptAnswers Kept = new(KeptAnswerBytes);
    private static long published;

    /// <summary>
    /// Which map an answer was read from. A table is an answer about one map, so the same dataset and arguments over two
    /// maps are two tables and need two handles: under one, the second install's rows replaced the first's, and a search
    /// of the first ran over the second.
    /// </summary>
    private static readonly ConditionalWeakTable<CabTable, StrongBox<long>> MapNumbers = new();
    private static long mapsNumbered;

    public static void Publish(string id, DataRole role, DataParam[] parameters, string description,
        TableProducer producer, bool cache = true, bool readerIndependent = false,
        Func<DataRequest, string>? cacheIdentity = null, object? cacheGate = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        Registered[Declared(id, parameters)] = new Dataset(id, role, parameters, description ?? string.Empty,
            producer, null) { Sequence = Next(id), Cache = cache, ReaderIndependent = readerIndependent,
                CacheIdentity = cacheIdentity, CacheGate = cacheGate };
        ClearCache();
    }

    public static void PublishBlob(string id, DataRole role, DataParam[] parameters, string description,
        BlobProducer producer, bool readerIndependent = false, bool readerTransition = false)
    {
        ArgumentNullException.ThrowIfNull(producer);
        if (readerIndependent && readerTransition) throw new ArgumentException("A reader transition is not reader-independent.");
        Registered[Declared(id, parameters)] = new Dataset(id, role, parameters, description ?? string.Empty,
            null, producer) { Sequence = Next(id), ReaderIndependent = readerIndependent, ReaderTransition = readerTransition };
        ClearCache();
    }

    public static void Remove(string id)
    {
        Registered.TryRemove(id, out _);
        ClearCache();
    }

    internal static bool IsCurrent(Dataset dataset) =>
        Registered.TryGetValue(dataset.Id, out Dataset? current) && ReferenceEquals(current, dataset);

    /// <summary>The place in the reading order this id holds. Re-publishing one keeps the place
    /// it already had, so a decoder reloaded mid-session does not reshuffle the surfaces.</summary>
    private static long Next(string id) =>
        Registered.TryGetValue(id, out Dataset? existing)
            ? existing.Sequence
            : Interlocked.Increment(ref published);

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

    public static void ClearCache()
    {
        Kept.Clear();
        Statements.StatementDatasets.Forget();
    }

    public static Dataset[] Available() =>
        Registered.Values.OrderBy(dataset => dataset.Sequence).ToArray();

    public static Dataset[] WithRole(DataRole role) =>
        Available().Where(dataset => dataset.Role == role).ToArray();

    public static (string Handle, ColumnTable Table) Table(string id, string[] namedArgs,
        CancellationToken cancellation = default, CabTable? map = null)
        => Table(Definition(id), namedArgs, cancellation, map);

    internal static (string Handle, ColumnTable Table) Table(Dataset dataset, string[] namedArgs,
        CancellationToken cancellation, CabTable? map)
    {
        if (dataset.CacheGate is { } gate)
        {
            lock (gate)
            {
                return TableCore(dataset, namedArgs, cancellation, map);
            }
        }
        return TableCore(dataset, namedArgs, cancellation, map);
    }

    private static (string Handle, ColumnTable Table) TableCore(Dataset dataset, string[] namedArgs,
        CancellationToken cancellation, CabTable? map)
    {
        string id = dataset.Id;
        Dictionary<string, string[]> values = Bind(dataset, namedArgs ?? []);
        bool cache = dataset.Cache;
        if (dataset.ReaderIndependent) cancellation.ThrowIfCancellationRequested();
        if (dataset.Table is null)
        {
            throw new InvalidOperationException($"dataset '{id}' is a blob, not a table -- ask for it with Blob().");
        }
        using DefaultDenormalScope denormals = DefaultDenormalScope.Enter();
        using DataUnit? unit = DataUnit.Current is null ? DataUnit.Begin() : null;
        DataRequest request = new(dataset, values, cancellation, map);
        string handle = HandleOf(id, values, map);
        if (dataset.CacheIdentity is { } identity)
        {
            StringBuilder key = new(handle);
            string contentIdentity = identity(request);
            Append(key, contentIdentity);
            handle = key.ToString();
        }
        if (cache && Kept.TryGet(handle, out ColumnTable? kept))
        {
            return (handle, kept);
        }
        ColumnTable table = dataset.Table(request);
        if (dataset.ReaderIndependent) cancellation.ThrowIfCancellationRequested();
        if (cache)
        {
            Kept.Offer(handle, table);
        }
        TableRegistry.Register(handle, table);
        return (handle, table);
    }

    /// <summary>A request to the dataset <paramref name="id"/>, its arguments bound and checked
    /// exactly as asking it would bind them -- for a verb that acts on what a request asked for
    /// rather than asking it again.</summary>
    public static DataRequest Request(string id, string[] namedArgs, CabTable? map = null)
    {
        (Dataset dataset, Dictionary<string, string[]> values) = Resolve(id, namedArgs);
        return new DataRequest(dataset, values, CancellationToken.None, map);
    }

    public static byte[] Blob(string id, string[] namedArgs, CancellationToken cancellation = default,
        CabTable? map = null, ReadOnlyMemory<byte> payload = default)
        => Blob(Definition(id), namedArgs, cancellation, map, payload);

    internal static byte[] Blob(Dataset dataset, string[] namedArgs, CancellationToken cancellation,
        CabTable? map, ReadOnlyMemory<byte> payload)
    {
        string id = dataset.Id;
        Dictionary<string, string[]> values = Bind(dataset, namedArgs ?? []);
        if (dataset.ReaderIndependent) cancellation.ThrowIfCancellationRequested();
        if (dataset.Blob is null)
        {
            throw new InvalidOperationException($"dataset '{id}' is a table, not a blob -- ask for it with Table().");
        }
        using DefaultDenormalScope denormals = DefaultDenormalScope.Enter();
        using DataUnit? unit = DataUnit.Current is null ? DataUnit.Begin() : null;
        return dataset.Blob(new DataRequest(dataset, values, cancellation, map, payload));
    }

    private static (Dataset Dataset, Dictionary<string, string[]> Values) Resolve(string id, string[] namedArgs)
    {
        Dataset dataset = Definition(id);
        return (dataset, Bind(dataset, namedArgs ?? []));
    }

    /// <summary>Use one publication for both lifecycle classification and execution.</summary>
    internal static Dataset Definition(string id)
    {
        if (!Registered.TryGetValue(id, out Dataset? dataset))
        {
            string known = string.Join(", ", Available().Select(entry => entry.Id));
            throw new InvalidOperationException(
                $"no dataset '{id}' is published. The active game publishes: "
                + $"{(known.Length == 0 ? "(none -- is a game hook enabled?)" : known)}");
        }
        return dataset;
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

    private static string HandleOf(string id, Dictionary<string, string[]> values, CabTable? map)
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
        if (map is not null)
        {
            handle.Append("|map:").Append(MapNumbers.GetValue(map, static _ => new StrongBox<long>(Interlocked.Increment(ref mapsNumbered))).Value);
        }
        return handle.ToString();
    }

    private static void Append(StringBuilder handle, string part) =>
        handle.Append('|').Append(part.Length).Append(':').Append(part);
}
