using System.Collections.Concurrent;
using System.Text;

namespace Ruri.RipperHook.Tables;

public static class TableRegistry
{
    private static readonly ConcurrentDictionary<string, ColumnSearch> Open = new(StringComparer.Ordinal);

    public static void Register(string handle, ColumnTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Open[handle] = new ColumnSearch(table);
    }

    public static ColumnSearch Opened(string handle)
        => Open.TryGetValue(handle, out ColumnSearch? search)
            ? search
            : throw new InvalidOperationException(
                $"no table is open under handle '{handle}' -- open it before searching it.");

    public static int[] Search(string handle, string query, IReadOnlyList<FilterRule>? rules)
        => Opened(handle).Search(query, rules);

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
            ColumnBuilder builder = new(ColumnKind.Text, rowCount);
            for (int row = 0; row < rowCount; row++)
            {
                builder.Add(flatValues[row * columns.Length + c]);
            }
            built[c] = builder.Build(columns[c]);
        }
        Register(handle, new ColumnTable { Name = handle, RowCount = rowCount, Columns = built });
        return handle;
    }
}
