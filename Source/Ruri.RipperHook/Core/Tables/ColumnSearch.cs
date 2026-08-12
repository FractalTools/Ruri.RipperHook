using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.Tables;

public sealed class ColumnSearch
{
    private readonly ColumnTable _table;
    private readonly Utf8Column[] _textColumns;
    private byte[][]? _folded;

    public ColumnSearch(ColumnTable table)
    {
        _table = table;
        _textColumns = table.Columns.OfType<Utf8Column>().ToArray();
    }

    public string Field(int row, string column)
    {
        foreach (Column candidate in _table.Columns)
        {
            if (!string.Equals(candidate.Name, column, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return candidate switch
            {
                Utf8Column text => text.Text(row),
                IntegerColumn integers => integers.Values[row].ToString(),
                RealColumn reals => reals.Values[row].ToString(),
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    public int[] Search(string query, IReadOnlyList<FilterRule>? rules = null)
    {
        int[] matched = QuickSearch(query);
        return RuleFilter.AnyEnabled(rules) ? RuleFilter.Apply(matched, rules!, Field) : matched;
    }

    private int[] QuickSearch(string query)
    {
        string trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            int[] all = new int[_table.RowCount];
            for (int row = 0; row < all.Length; row++)
            {
                all[row] = row;
            }
            return all;
        }

        byte[][] folded = _folded ??= _textColumns.Select(static column =>
            Utf8Search.FoldBlob(column.Blob, column.Blob.Length)).ToArray();

        byte[] needle = Utf8Search.FoldNeedle(trimmed);
        bool[] mask = new bool[_table.RowCount];
        for (int i = 0; i < _textColumns.Length; i++)
        {
            Utf8Search.ScanColumn(folded[i], _textColumns[i].Offsets, needle, mask,
                static (rowMask, row) => rowMask[row] = true);
        }

        int count = 0;
        foreach (bool hit in mask)
        {
            if (hit)
            {
                count++;
            }
        }
        int[] rows = new int[count];
        int next = 0;
        for (int row = 0; row < mask.Length; row++)
        {
            if (mask[row])
            {
                rows[next++] = row;
            }
        }
        return rows;
    }
}
