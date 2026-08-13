using Ruri.RipperHook.Tables;

namespace Ruri.RipperHook.CabMapping;

public readonly record struct CabPathRow(int CabId, int PathIndex);

public static class CabPathQuery
{
    public const string CabField = "cab";
    public const string ContainerField = "container";
    public const string FolderField = "folder";
    public const string LeafField = "leaf";
    public const string StemField = "stem";
    public const string ExtensionField = "extension";
    public const string TypeNamesField = "type_names";
    public const string SourceField = "source";
    public const string DependencyCountField = "deps";

    public static readonly string[] Fields =
        [CabField, ContainerField, FolderField, LeafField, StemField, ExtensionField,
         TypeNamesField, SourceField, DependencyCountField];

    public static CabPathRow[] Rows(CabTable table, string query, IReadOnlyList<FilterRule>? rules)
    {
        ArgumentNullException.ThrowIfNull(table);
        CabTableSearch search = CabTableSearch.For(table);
        int[] cabIds = search.Search(query ?? string.Empty, null, string.Empty, 0);

        int total = 0;
        foreach (int cabId in cabIds)
        {
            total += table.ContainerPathCount(cabId);
        }
        CabPathRow[] expanded = new CabPathRow[total];
        int cursor = 0;
        foreach (int cabId in cabIds)
        {
            int paths = table.ContainerPathCount(cabId);
            for (int pathIndex = 0; pathIndex < paths; pathIndex++)
            {
                expanded[cursor++] = new CabPathRow(cabId, pathIndex);
            }
        }

        if (!RuleFilter.AnyEnabled(rules))
        {
            return Ordered(table, expanded);
        }
        int[] positions = new int[expanded.Length];
        for (int index = 0; index < positions.Length; index++)
        {
            positions[index] = index;
        }
        int[] kept = RuleFilter.Apply(positions, rules!,
            (position, field) => Field(table, search, expanded[position], field));
        CabPathRow[] matched = new CabPathRow[kept.Length];
        for (int index = 0; index < kept.Length; index++)
        {
            matched[index] = expanded[kept[index]];
        }
        return Ordered(table, matched);
    }

    private static CabPathRow[] Ordered(CabTable table, CabPathRow[] rows)
    {
        int[] order = new int[rows.Length];
        for (int index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }
        Array.Sort(order, (left, right) =>
        {
            int byPath = table.ContainerPathUtf8(rows[left].CabId, rows[left].PathIndex)
                .SequenceCompareTo(table.ContainerPathUtf8(rows[right].CabId, rows[right].PathIndex));
            return byPath != 0 ? byPath : rows[left].CabId.CompareTo(rows[right].CabId);
        });
        CabPathRow[] sorted = new CabPathRow[rows.Length];
        for (int index = 0; index < sorted.Length; index++)
        {
            sorted[index] = rows[order[index]];
        }
        return sorted;
    }

    public static string Container(CabTable table, CabPathRow row) =>
        table.ContainerPath(row.CabId, row.PathIndex);

    public static string Field(CabTable table, CabPathRow row, string field) =>
        Field(table, CabTableSearch.For(table), row, field);

    private static string Field(CabTable table, CabTableSearch search, CabPathRow row, string field)
    {
        switch (field)
        {
            case ContainerField:
                return Container(table, row);
            case FolderField:
            {
                string path = Container(table, row);
                int slash = path.LastIndexOf('/');
                return slash < 0 ? string.Empty : path[..slash];
            }
            case LeafField:
                return Leaf(table, row);
            case StemField:
            {
                string leaf = Leaf(table, row);
                int dot = leaf.LastIndexOf('.');
                return dot < 0 ? leaf : leaf[..dot];
            }
            case ExtensionField:
            {
                string leaf = Leaf(table, row);
                int dot = leaf.LastIndexOf('.');
                return dot < 0 ? string.Empty : leaf[(dot + 1)..];
            }
            default:
                return search.Field(row.CabId, field);
        }
    }

    private static string Leaf(CabTable table, CabPathRow row)
    {
        string path = Container(table, row);
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static FilterRule[] ParseRules(IEnumerable<string> declared)
    {
        List<FilterRule> rules = [];
        foreach (string entry in declared)
        {
            string[] fields = entry.Split('|');
            if (fields.Length is < 3 or > 4)
            {
                throw new ArgumentException(
                    $"rule '{entry}' must be field|relation|value[|exclude].");
            }
            if (!Fields.Contains(fields[0], StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"rule '{entry}' selects on unknown field '{fields[0]}'; known fields are {string.Join(", ", Fields)}.");
            }
            bool include = fields.Length < 4 || !string.Equals(fields[3], "exclude", StringComparison.OrdinalIgnoreCase);
            rules.Add(new FilterRule(fields[0], fields[1], fields[2], include, true));
        }
        return rules.ToArray();
    }
}
