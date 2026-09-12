using System.Text;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.Views;

public readonly record struct ViewSpec(
    string Table,
    string Facet,
    string Search,
    FilterRule[] Rules,
    string Note,
    bool ShippedOnly,
    string SortColumn,
    int SortDirection,
    int Window);

public sealed class View : IDisposable
{
    public const string EveryFacet = "*";
    private const string GroupColumnName = "is_group";
    private const string LabelColumnName = "label";

    private readonly Column? _keys;
    private PinnedTable? _packedRows;
    private PinnedTable? _packedFacets;

    private View(ViewSpec spec, ColumnTable source, ColumnTable rows, ColumnTable facets,
        int matched, int shown, string summary)
    {
        Spec = spec;
        Source = source;
        Rows = rows;
        Facets = facets;
        Matched = matched;
        Shown = shown;
        Summary = summary;
        _keys = rows.FirstWithRole(ColumnRole.Key);
    }

    public ViewSpec Spec { get; }

    public ColumnTable Source { get; }

    public ColumnTable Rows { get; }

    public ColumnTable Facets { get; }

    public int Matched { get; }

    public int Shown { get; }

    public string Summary { get; }

    public PinnedTable PackedRows => _packedRows ??= ColumnTablePacking.Pin(Spec.Table, Rows);

    public PinnedTable PackedFacets => _packedFacets ??= ColumnTablePacking.Pin(Spec.Table, Facets);

    public void Dispose()
    {
        Interlocked.Exchange(ref _packedRows, null)?.Dispose();
        Interlocked.Exchange(ref _packedFacets, null)?.Dispose();
    }

    public static View Open(string table, string facet, string search, string[]? flatRules, string note,
        bool shippedOnly, string sortColumn, int sortDirection, int window) =>
        Compose(new ViewSpec(table, facet ?? string.Empty, search ?? string.Empty,
            RuleFilter.Parse(flatRules), note ?? string.Empty, shippedOnly,
            sortColumn ?? string.Empty, sortDirection, window), TableRegistry.Opened(table));

    public int IndexOfKey(string key)
    {
        if (_keys is null || key.Length == 0)
        {
            return -1;
        }
        byte[] wanted = Encoding.UTF8.GetBytes(key);
        for (int row = 0; row < Rows.RowCount; row++)
        {
            if (_keys.Bytes(row).SequenceEqual(wanted))
            {
                return row;
            }
        }
        return -1;
    }

    public static View Compose(ViewSpec spec, ColumnSearch search)
    {
        ColumnTable source = search.Table;
        Column[] labels = source.WithRole(ColumnRole.Label);
        Column? facet = source.FirstWithRole(ColumnRole.Facet);
        Column? group = source.FirstWithRole(ColumnRole.Group);
        Column? named = source.FirstWithRole(ColumnRole.Named);
        Column? shipped = source.FirstWithRole(ColumnRole.Shipped);

        int[] matched = Narrow(search.Search(spec.Search, spec.Rules), null, string.Empty,
            spec.ShippedOnly ? shipped : null);
        ColumnTable facets = CountFacets(facet, matched, spec.Facet);
        int[] kept = Narrow(matched, facet, spec.Facet, null);
        Sort(kept, source, spec, labels, group, named);

        int window = spec.Window > 0 ? Math.Min(spec.Window, kept.Length) : kept.Length;
        (int[] plan, string[] headers) = Plan(kept, group, window);
        ColumnTable rows = Materialize(source, plan, headers, labels);
        return new View(spec, source, rows, facets, kept.Length, window,
            Describe(kept.Length, source.RowCount, window, spec.Note));
    }

    private static string Describe(int kept, int total, int shown, string note)
    {
        StringBuilder said = new();
        said.Append(kept).Append(" of ").Append(total).Append(" row(s)");
        if (note.Length != 0)
        {
            said.Append(" · ").Append(note);
        }
        if (shown < kept)
        {
            said.Append(" · showing ").Append(shown).Append(", narrow your search to see the rest");
        }
        return said.ToString();
    }

    private static ColumnTable CountFacets(Column? facet, int[] matched, string picked)
    {
        TableBuilder facets = new("facets", "id", "label", "detail");
        facets.Role(ColumnRole.Key, "id").Role(ColumnRole.Label, "label").Role(ColumnRole.Detail, "detail");
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        if (facet is not null)
        {
            foreach (int row in matched)
            {
                string value = facet.Text(row);
                seen[value] = seen.GetValueOrDefault(value) + 1;
            }
        }
        if (seen.Count < 2)
        {
            return facets.Build();
        }
        // Whatever the switch is CURRENTLY set to stays on it even when nothing
        // matches it any more: a search that empties the picked kind must not
        // silently move the user to a different one.
        if (picked.Length != 0 && picked != EveryFacet)
        {
            seen.TryAdd(picked, 0);
        }
        facets.Row(EveryFacet, "All", $"{matched.Length} row(s)");
        foreach ((string value, int count) in seen.OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            facets.Row(value.Length == 0 ? EveryFacet : value,
                value.Length == 0 ? "Unfiled" : value, $"{count} row(s)");
        }
        return facets.Build();
    }

    private static int[] Narrow(int[] matched, Column? facet, string wanted, Column? shipped)
    {
        bool everyFacet = facet is null || wanted.Length == 0 || wanted == EveryFacet;
        if (everyFacet && shipped is null)
        {
            return matched;
        }
        byte[] needle = everyFacet ? [] : Encoding.UTF8.GetBytes(wanted);
        List<int> kept = new(matched.Length);
        foreach (int row in matched)
        {
            if (shipped is not null && !shipped.Truthy(row))
            {
                continue;
            }
            if (!everyFacet && !facet!.Bytes(row).SequenceEqual(needle))
            {
                continue;
            }
            kept.Add(row);
        }
        return kept.ToArray();
    }

    private static void Sort(int[] rows, ColumnTable source, ViewSpec spec, Column[] labels,
        Column? group, Column? named)
    {
        if (spec.SortColumn.Length != 0 && spec.SortDirection != 0)
        {
            Column column = source[spec.SortColumn];
            int sign = spec.SortDirection == 2 ? -1 : 1;
            Comparison<int> byColumn = column.Sliced
                ? (left, right) => sign * Compare(column.Bytes(left), column.Bytes(right))
                : (left, right) => sign * column.Real(left).CompareTo(column.Real(right));
            Array.Sort(rows, byColumn);
            return;
        }
        Array.Sort(rows, (left, right) =>
        {
            if (named is not null)
            {
                int rank = (named.Bytes(left).IsEmpty ? 1 : 0) - (named.Bytes(right).IsEmpty ? 1 : 0);
                if (rank != 0)
                {
                    return rank;
                }
            }
            if (group is not null)
            {
                int sections = Compare(group.Bytes(left), group.Bytes(right));
                if (sections != 0)
                {
                    return sections;
                }
            }
            return Compare(Label(labels, left), Label(labels, right));
        });
    }

    private static ReadOnlySpan<byte> Label(Column[] labels, int row)
    {
        foreach (Column column in labels)
        {
            ReadOnlySpan<byte> value = column.Bytes(row);
            if (!value.IsEmpty)
            {
                return value;
            }
        }
        return default;
    }

    private static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int shared = Math.Min(left.Length, right.Length);
        for (int index = 0; index < shared; index++)
        {
            byte a = Fold(left[index]);
            byte b = Fold(right[index]);
            if (a != b)
            {
                return a < b ? -1 : 1;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    private static byte Fold(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value | 0x20) : value;

    private static (int[] Plan, string[] Headers) Plan(int[] kept, Column? group, int window)
    {
        if (group is null)
        {
            return (kept[..window], new string[window]);
        }
        List<int> plan = new(window + 16);
        List<string> headers = new(window + 16);
        int cursor = 0;
        while (cursor < window)
        {
            string section = group.Text(kept[cursor]);
            int last = cursor;
            while (last < window && group.Text(kept[last]) == section)
            {
                last++;
            }
            if (section.Length != 0)
            {
                plan.Add(-1);
                headers.Add($"{section}  ({last - cursor})");
            }
            for (; cursor < last; cursor++)
            {
                plan.Add(kept[cursor]);
                headers.Add(string.Empty);
            }
        }
        return (plan.ToArray(), headers.ToArray());
    }

    private static ColumnTable Materialize(ColumnTable source, int[] plan, string[] headers, Column[] labels)
    {
        List<Column> built = [];
        ColumnBuilder names = new(ColumnKind.Text, plan.Length);
        ColumnBuilder sections = new(ColumnKind.Integer, plan.Length);
        for (int drawn = 0; drawn < plan.Length; drawn++)
        {
            bool header = plan[drawn] < 0;
            names.Add(header ? Encoding.UTF8.GetBytes(headers[drawn]) : Label(labels, plan[drawn]));
            sections.Add(header ? 1L : 0L);
        }
        built.Add(names.Build(LabelColumnName, ColumnRole.Label));
        built.Add(sections.Build(GroupColumnName));
        foreach (Column column in source.Columns)
        {
            if (string.Equals(column.Name, LabelColumnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(column.Name, GroupColumnName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ColumnBuilder builder = new(column.Kind, plan.Length);
            foreach (int row in plan)
            {
                if (row < 0)
                {
                    builder.AddBlank();
                }
                else
                {
                    builder.Add(column.Bytes(row));
                }
            }
            built.Add(builder.Build(column.Name, column.Role & ~ColumnRole.Label));
        }
        return new ColumnTable
        {
            Name = source.Name,
            RowCount = plan.Length,
            Columns = built.ToArray(),
        };
    }
}
