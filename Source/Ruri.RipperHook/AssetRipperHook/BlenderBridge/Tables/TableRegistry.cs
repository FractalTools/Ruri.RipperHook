using System.Collections.Concurrent;
using System.Text;
using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.BlenderBridge.Tables;

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

    /// <summary>A list the caller assembled, published as a table so it gets the one search,
    /// the one rule evaluator and the one view engine every other list gets. Columns are
    /// spelled as they are anywhere else ("name", "count#", "name|Displayed Name"), and
    /// <paramref name="roles"/> states positionally what each one answers.</summary>
    public static string OpenHostTable(string handle, string[] columns, int[]? roles,
        string[] flatValues)
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
        TableBuilder table = new(handle, columns);
        if (roles is { Length: > 0 })
        {
            table.Roles(roles.Select(role => (ColumnRole)role).ToArray());
        }
        foreach (string value in flatValues)
        {
            table.Add(value);
        }
        Register(handle, table.Build());
        return handle;
    }
}
