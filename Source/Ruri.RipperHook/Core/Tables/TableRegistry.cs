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

    public static int[] Search(string handle, string query, IReadOnlyList<FilterRule>? rules)
        => Open.TryGetValue(handle, out ColumnSearch? search)
            ? search.Search(query, rules)
            : throw new InvalidOperationException(
                $"no table is open under handle '{handle}' -- open it before searching it.");

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
