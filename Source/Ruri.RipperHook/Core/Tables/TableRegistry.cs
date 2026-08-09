using System.Collections.Concurrent;
using System.Text;

namespace Ruri.RipperHook.Tables;

/// <summary>
/// The open searchable tables, by handle. One registry for every source of rows there is: a game
/// container projected out of the VFS, or a list the host assembled itself
/// (<see cref="OpenHostTable"/>). Searching one needs no game hook -- only PROJECTING a VFS
/// container does -- so a host's own list is searchable on the same engine with the same rules
/// regardless of which game, if any, is hooked.
/// </summary>
public static class TableRegistry
{
    private static readonly ConcurrentDictionary<string, ColumnSearch> Open = new(StringComparer.Ordinal);

    /// <summary>Publish a table under <paramref name="handle"/>, replacing whatever was there.
    /// Re-registering is how a refreshed list stays searchable under the handle its host already
    /// holds.</summary>
    public static void Register(string handle, ColumnTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Open[handle] = new ColumnSearch(table);
    }

    /// <summary>Row ids of the table under <paramref name="handle"/> matching the query and every
    /// enabled rule.</summary>
    public static int[] Search(string handle, string query, IReadOnlyList<FilterRule>? rules)
        => Open.TryGetValue(handle, out ColumnSearch? search)
            ? search.Search(query, rules)
            : throw new InvalidOperationException(
                $"no table is open under handle '{handle}' -- open it before searching it.");

    /// <summary>Publish a host-assembled list of TEXT columns and return its handle.
    /// <paramref name="flatValues"/> is row-major: <c>columns.Length</c> values per row, so the
    /// caller ships one flat string array and nothing per-row is parsed on either side.
    ///
    /// This is what puts a list the host built (a scene list, a landmark list) on exactly the same
    /// vectorized search and the same rule evaluator as the cabmap browser, instead of each host
    /// growing its own matching code.</summary>
    public static string OpenHostTable(string handle, string[] columns, string[] flatValues)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(flatValues);
        if (columns.Length == 0)
        {
            throw new ArgumentException("a table needs at least one column", nameof(columns));
        }
        if (flatValues.Length % columns.Length != 0)
        {
            throw new ArgumentException(
                $"flatValues length {flatValues.Length} is not a multiple of the {columns.Length} column(s)",
                nameof(flatValues));
        }
        int rowCount = flatValues.Length / columns.Length;
        Column[] built = new Column[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            Utf8ColumnBuilder builder = new(rowCount);
            for (int row = 0; row < rowCount; row++)
            {
                builder.Add(Encoding.UTF8.GetBytes(flatValues[row * columns.Length + c] ?? string.Empty));
            }
            built[c] = builder.Build(columns[c]);
        }
        Register(handle, new ColumnTable { Name = handle, RowCount = rowCount, Columns = built });
        return handle;
    }
}
