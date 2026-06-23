using System.Runtime.InteropServices;
using AssetRipper.SourceGenerated;
using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI;

/// <summary>
/// Virtual-file browser over a loaded CAB map: every CAB is one row (a "virtual file") shown immediately
/// after Load CABMap — no game load yet. Search narrows by the readable Container paths (e.g. "pelica") when
/// a name index is present. Selection drives on-demand work through the parent <see cref="MainForm"/>:
/// "Load selected" does a bundle-granular load (only those bundles, not whole chunks) so you can preview
/// them; "Export with dependencies" loads each selection's full transitive dependency closure and runs a
/// real AssetRipper export — the unitypackage-style "this asset plus everything it needs".
/// </summary>
internal sealed class CabFileBrowser : Form
{
    private readonly MainForm _parent;
    private readonly IReadOnlyList<ExportCabMap.CabRow> _allRows;
    private readonly List<ExportCabMap.CabRow> _viewRows = [];

    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter by name / container path / source / type (regex)…" };
    private readonly ListView _listView = new();
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ContextMenuStrip _contextMenu = new();

    // rows are materialised + sorted off the UI thread by the caller (258k+ entries).
    public CabFileBrowser(MainForm parent, IReadOnlyList<ExportCabMap.CabRow> rows, bool hasNames)
    {
        _parent = parent;
        _allRows = rows;

        Text = "CAB Virtual Files";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new System.Drawing.Size(820, 520);
        ClientSize = new System.Drawing.Size(1180, 700);
        Location = new System.Drawing.Point(parent.Location.X + 60, parent.Location.Y + 60);

        InitializeLayout(hasNames);
        ApplyFilter();
    }

    private void InitializeLayout(bool hasNames)
    {
        Panel searchPanel = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(4, 3, 4, 3) };
        _searchBox.KeyDown += SearchBox_KeyDown;
        searchPanel.Controls.Add(_searchBox);

        _listView.Dock = DockStyle.Fill;
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.HideSelection = false;
        _listView.MultiSelect = true;
        _listView.VirtualMode = true;
        _listView.Columns.Add("Name", 320);
        _listView.Columns.Add("Container path", 460);
        _listView.Columns.Add("Source (.chk)", 230);
        _listView.Columns.Add("Types", 200);
        _listView.Columns.Add("Deps", 60);
        _listView.RetrieveVirtualItem += ListView_RetrieveVirtualItem;
        _listView.KeyDown += ListView_KeyDown;
        _listView.MouseClick += ListView_MouseClick;

        ToolStripMenuItem loadItem = new("Load selected (preview)");
        ToolStripMenuItem appendItem = new("Load selected (append)");
        ToolStripMenuItem exportItem = new("Export with dependencies…");
        ToolStripMenuItem copyItem = new("Copy container path");
        loadItem.Click += (_, _) => LoadSelected(append: false);
        appendItem.Click += (_, _) => LoadSelected(append: true);
        exportItem.Click += (_, _) => ExportSelectedWithDeps();
        copyItem.Click += (_, _) => CopySelectedPaths();
        _contextMenu.Items.AddRange([loadItem, appendItem, new ToolStripSeparator(), exportItem, new ToolStripSeparator(), copyItem]);

        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Dock = DockStyle.Bottom;

        Panel listPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 0) };
        listPanel.Controls.Add(_listView);

        Controls.Add(listPanel);
        Controls.Add(searchPanel);
        Controls.Add(_statusStrip);

        if (!hasNames)
        {
            _statusLabel.Text = "No name index (.names) loaded — rows show CAB hashes. Build one with the CLI --build-name-index to search by name.";
        }
    }

    // ── filtering ───────────────────────────────────────────────────────────────────────────────
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }
        e.SuppressKeyPress = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = _searchBox.Text.Trim();
        _viewRows.Clear();
        if (query.Length == 0)
        {
            _viewRows.AddRange(_allRows);
        }
        else
        {
            System.Text.RegularExpressions.Regex? regex = null;
            try
            {
                regex = new System.Text.RegularExpressions.Regex(query, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                _statusLabel.Text = "Invalid regex.";
            }

            foreach (ExportCabMap.CabRow row in _allRows)
            {
                if (RowMatches(row, query, regex))
                {
                    _viewRows.Add(row);
                }
            }
        }

        _listView.VirtualListSize = _viewRows.Count;
        _listView.Invalidate();
        UpdateStatus();
    }

    private static bool RowMatches(ExportCabMap.CabRow row, string query, System.Text.RegularExpressions.Regex? regex)
    {
        bool Match(string value) => regex is not null ? regex.IsMatch(value) : value.Contains(query, StringComparison.OrdinalIgnoreCase);

        if (Match(row.Cab) || Match(row.RelativePath) || Match(TypeNames(row)))
        {
            return true;
        }
        foreach (string path in row.ContainerPaths)
        {
            if (Match(path)) return true;
        }
        return false;
    }

    // ── rendering ───────────────────────────────────────────────────────────────────────────────
    private void ListView_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if ((uint)e.ItemIndex >= (uint)_viewRows.Count)
        {
            return;
        }
        ExportCabMap.CabRow row = _viewRows[e.ItemIndex];
        ListViewItem item = new(DisplayName(row));
        item.SubItems.Add(row.ContainerPaths.Count > 0 ? string.Join("  |  ", row.ContainerPaths) : row.Cab);
        item.SubItems.Add(row.RelativePath);
        item.SubItems.Add(TypeNames(row));
        item.SubItems.Add(row.DependencyCount.ToString());
        e.Item = item;
    }

    private static string DisplayName(ExportCabMap.CabRow row)
    {
        if (row.ContainerPaths.Count == 0)
        {
            return row.Cab;
        }
        string first = row.ContainerPaths[0];
        int slash = first.LastIndexOf('/');
        string leaf = slash >= 0 ? first[(slash + 1)..] : first;
        return row.ContainerPaths.Count > 1 ? $"{leaf}  (+{row.ContainerPaths.Count - 1})" : leaf;
    }

    private static string TypeNames(ExportCabMap.CabRow row)
    {
        return string.Join(", ", row.ClassIds
            .Where(static id => id != (int)ClassIDType.AssetBundle)
            .Select(static id => Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : id.ToString())
            .DefaultIfEmpty("AssetBundle"));
    }

    // ── selection → parent actions ────────────────────────────────────────────────────────────────
    private void ListView_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && _listView.SelectedIndices.Count > 0)
        {
            _contextMenu.Show(_listView, e.Location);
        }
    }

    private void ListView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.A)
        {
            // Ctrl+A — select every visible row. A virtual-mode ListView has no managed select-all, and
            // adding indices one by one is O(n²); the native LVM_SETITEMSTATE with iItem -1 selects all at once.
            e.SuppressKeyPress = true;
            if (_viewRows.Count > 0)
            {
                LvItem state = new() { StateMask = ListViewItemSelected, State = ListViewItemSelected };
                SendMessage(_listView.Handle, ListViewSetItemState, (IntPtr)(-1), ref state);
            }
        }
    }

    private const int ListViewSetItemState = 0x102B; // LVM_SETITEMSTATE
    private const int ListViewItemSelected = 0x0002; // LVIS_SELECTED

    [StructLayout(LayoutKind.Sequential)]
    private struct LvItem
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public IntPtr Text;
        public int TextMax;
        public int Image;
        public IntPtr LParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, ref LvItem lParam);

    private IReadOnlyList<string> SelectedCabs()
    {
        List<string> cabs = [];
        foreach (int index in _listView.SelectedIndices)
        {
            if ((uint)index < (uint)_viewRows.Count)
            {
                cabs.Add(_viewRows[index].Cab);
            }
        }
        return cabs;
    }

    private async void LoadSelected(bool append)
    {
        IReadOnlyList<string> cabs = SelectedCabs();
        if (cabs.Count == 0)
        {
            return;
        }
        _statusLabel.Text = $"Loading {cabs.Count} CAB(s)…";
        await _parent.LoadCabsScopedAsync(cabs, append);
        UpdateStatus();
    }

    private async void ExportSelectedWithDeps()
    {
        IReadOnlyList<string> cabs = SelectedCabs();
        if (cabs.Count == 0)
        {
            return;
        }
        using FolderBrowserDialog dialog = new() { Description = "Export selected CABs + all dependencies to…", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        _statusLabel.Text = $"Exporting {cabs.Count} CAB(s) with dependencies…";
        await _parent.ExportCabsWithDepsAsync(cabs, dialog.SelectedPath);
        UpdateStatus();
    }

    private void CopySelectedPaths()
    {
        List<string> lines = [];
        foreach (int index in _listView.SelectedIndices)
        {
            if ((uint)index < (uint)_viewRows.Count)
            {
                ExportCabMap.CabRow row = _viewRows[index];
                lines.Add(row.ContainerPaths.Count > 0 ? string.Join(Environment.NewLine, row.ContainerPaths) : row.Cab);
            }
        }
        if (lines.Count > 0)
        {
            Clipboard.SetDataObject(string.Join(Environment.NewLine, lines));
        }
    }

    private void UpdateStatus()
    {
        _statusLabel.Text = _viewRows.Count == _allRows.Count
            ? $"{_allRows.Count:N0} CABs"
            : $"{_viewRows.Count:N0} / {_allRows.Count:N0} CABs";
    }
}
