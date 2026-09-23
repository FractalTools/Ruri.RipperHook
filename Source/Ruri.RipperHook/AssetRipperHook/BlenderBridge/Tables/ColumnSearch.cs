using Ruri.RipperHook.CabMapping;

namespace Ruri.RipperHook.BlenderBridge.Tables;

public sealed class ColumnSearch
{
    private readonly ColumnTable _table;
    private readonly Column[] _textColumns;
    private byte[][]? _folded;

    public ColumnSearch(ColumnTable table)
    {
        _table = table;
        _textColumns = table.Columns.Where(column => column.Kind == ColumnKind.Text).ToArray();
    }

    public ColumnTable Table => _table;

    public string Field(int row, string column) => _table.Find(column)?.Text(row) ?? string.Empty;

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
            Utf8Search.FoldBlob(column.Data, column.Data.Length)).ToArray();

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
