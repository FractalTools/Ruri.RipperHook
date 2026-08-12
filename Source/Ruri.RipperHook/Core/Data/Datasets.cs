using System.Collections.Concurrent;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Data;

/// <summary>
/// Everything a game publishes, and the only way any of it reaches a host.
///
/// <para>A game hook does not grow the host. It REGISTERS datasets here, and a host asks for one
/// by id. There are exactly two primitives, because there turned out to be exactly two kinds of
/// thing that ever crossed: a table of rows, and a blob of bytes. Every per-game entry point that
/// used to sit on the bridge -- scene landmarks, npc parts, npc materials, character models, a
/// projected config container, a customization catalog, a face's pattern table -- is one row shape
/// and is now one dataset.</para>
///
/// <para><b>Why one shape is the fast shape.</b> <see cref="ColumnTable"/> is columnar: UTF-8 blobs
/// plus offset arrays, no System.String per row, one interop crossing. Because nothing else is
/// allowed across, every dataset of every game inherits -- for free, with no code of its own --
/// the vectorized ASCII-fold search, the shared rule evaluator, sorting, row subsetting and the
/// host-side numpy views. A per-game DTO inherits none of that, which is why the ones that existed
/// could not be searched, filtered or cached at all.</para>
///
/// <para><b>Nested data is normalized, never nested.</b> A row that owns a list (a placement's
/// material paths, a face channel's pattern pairs) publishes a CHILD dataset keyed by the parent's
/// row index. That is what keeps the shape count at one instead of one per structure.</para>
///
/// <para><b>Results are cached by (id, args).</b> Reading is a producer's only job; being read
/// twice is the registry's problem. A host may ask for the same dataset on every redraw.</para>
/// </summary>
public static class Datasets
{
    /// <summary>Reads one dataset. Args are positional and declared by the registration.</summary>
    public delegate ColumnTable TableProducer(DataRequest request);

    /// <summary>Reads one binary payload -- the one thing that is not a table.</summary>
    public delegate byte[] BlobProducer(DataRequest request);

    /// <summary>
    /// One publishable thing: what it is called, what it needs, what it is, and how to read it.
    /// <paramref name="Parameters"/> names the positional arguments, so a wrong call is refused
    /// with the names it wanted rather than an IndexOutOfRange from inside a producer -- and so a
    /// host can DISCOVER what the active game offers instead of hardcoding ids.
    /// </summary>
    public sealed record Dataset(
        string Id,
        string[] Parameters,
        string Description,
        TableProducer? Table,
        BlobProducer? Blob);

    private static readonly ConcurrentDictionary<string, Dataset> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ColumnTable> Cache = new(StringComparer.Ordinal);

    /// <summary>Publish a table dataset. Called from a game hook's own initialization; re-registering
    /// the same id replaces it, which is what enabling a different version of the same game does.</summary>
    public static void Register(string id, string[] parameters, string description, TableProducer producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(producer);
        Registered[id] = new Dataset(id, parameters ?? [], description ?? string.Empty, producer, null);
    }

    /// <summary>Publish a blob dataset -- a payload that is bytes rather than rows.</summary>
    public static void RegisterBlob(string id, string[] parameters, string description, BlobProducer producer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(producer);
        Registered[id] = new Dataset(id, parameters ?? [], description ?? string.Empty, null, producer);
    }

    /// <summary>
    /// Forget everything a game published. Called when a game hook is torn down: game hooks are
    /// mutually exclusive (see <see cref="Ruri.Hook.RuriHook.ApplyHooks"/>), so the datasets of the
    /// game being switched away from must not stay answerable.
    /// </summary>
    public static void Clear(string idPrefix)
    {
        foreach (string id in Registered.Keys.Where(key => key.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            Registered.TryRemove(id, out _);
        }
        ClearCache();
    }

    public static void ClearCache() => Cache.Clear();

    /// <summary>Every dataset the active game publishes, id-sorted -- what a host lists to find out
    /// what it can ask for.</summary>
    public static Dataset[] Available() =>
        Registered.Values.OrderBy(dataset => dataset.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// One dataset as a table, cached by (id, args) and published to <see cref="TableRegistry"/>
    /// under that same key -- which IS the search handle, so a host searches what it just read
    /// without a second registration step.
    /// </summary>
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

    /// <summary>One dataset as bytes. Not cached: a blob is asked for when it is about to be
    /// consumed, and holding megabytes on the chance of a second ask is the wrong trade.</summary>
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

    /// <summary>The cache key, which is also the search handle. Args are part of it because a
    /// dataset read with different arguments is a different table. Joined on a unit
    /// separator, which cannot occur in an id, a path or a column name.</summary>
    private const string ArgumentSeparator = "\u001f";

    private static string HandleOf(string id, string[] args) =>
        args is { Length: > 0 }
            ? id + ArgumentSeparator + string.Join(ArgumentSeparator, args)
            : id;
}
