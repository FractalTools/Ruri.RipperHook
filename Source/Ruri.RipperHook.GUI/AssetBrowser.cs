using Ruri.RipperHook.GUI.Services;
using System.Text.RegularExpressions;

namespace Ruri.RipperHook.GUI;

internal sealed class AssetBrowser : Form
{
	private static readonly string[] FilterFields = ["Name", "Container", "Source", "PathID", "Type"];

	private readonly MainForm _parent;
	private readonly AssetMapBrowserService _service = new();
	private readonly List<AssetMapEntry> _assetEntries = [];
	private readonly Dictionary<string, string> _filters = FilterFields.ToDictionary(static x => x, static _ => string.Empty, StringComparer.OrdinalIgnoreCase);
	private readonly ClickAwayFilter _clickAwayFilter;

	private readonly MenuStrip _menuStrip = new();
	private readonly ToolStripMenuItem _fileMenu = new("File");
	private readonly ToolStripMenuItem _assetMenu = new("Asset");
	private readonly ToolStripMenuItem _optionsMenu = new("Options");
	private readonly ToolStripMenuItem _miscMenu = new("Misc.");
	private readonly ToolStripMenuItem _loadAssetMapMenuItem = new("Load AssetMap");
	private readonly ToolStripMenuItem _loadCabMapMenuItem = new("Load CABMap");
	private readonly ToolStripMenuItem _resetMenuItem = new("Reset");
	private readonly ToolStripMenuItem _loadSelectedMenuItem = new("Load selected");
	private readonly ToolStripMenuItem _appendSelectedMenuItem = new("Load selected (append)");
	private readonly ToolStripMenuItem _exportCabMenuItem = new("Export CAB for selected assets");
	private readonly ToolStripMenuItem _fastLoadMenuItem = new("Prefer CABMap-backed source resolution") { Checked = true, CheckOnClick = true };
	private readonly ToolStripMenuItem _convertAssetMapToJsonMenuItem = new("Convert AssetMap to json");
	private readonly ToolStripMenuItem _clearSearchHistoryMenuItem = new("Clear search history");
	private readonly TableLayoutPanel _filtersPanel = new();
	private readonly TextBox _nameTextBox = new() { PlaceholderText = "Name", Tag = "Name", Dock = DockStyle.Fill };
	private readonly TextBox _containerTextBox = new() { PlaceholderText = "Container", Tag = "Container", Dock = DockStyle.Fill };
	private readonly TextBox _sourceTextBox = new() { PlaceholderText = "Source", Tag = "Source", Dock = DockStyle.Fill };
	private readonly TextBox _pathTextBox = new() { PlaceholderText = "PathID", Tag = "PathID", Dock = DockStyle.Fill };
	private readonly TextBox _typeTextBox = new() { PlaceholderText = "Type", Tag = "Type", Dock = DockStyle.Fill };
	private readonly ListView _assetListView = new();
	private readonly StatusStrip _statusStrip = new();
	private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Load an AssetMap from File menu" };
	private readonly ContextMenuStrip _contextMenuStrip = new();
	private readonly ListBox _historyListBox = new() { Visible = false, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };

	private TextBox? _activeSearchTextBox;
	private bool _historyListClicked;
	private bool _suppressTextChanged;
	private bool _suppressShowHistory;
	private int _sortColumn = -1;
	private bool _reverseSort;
	private int _clickedColumnIndex;
	private int _totalCount;

	public AssetBrowser(MainForm parent)
	{
		_parent = parent;
		Text = "Asset Browser";
		StartPosition = FormStartPosition.Manual;
		MinimumSize = new System.Drawing.Size(760, 480);
		ClientSize = new System.Drawing.Size(1264, 681);
		Location = new System.Drawing.Point(parent.Location.X + Math.Max(0, (parent.Width - Width) / 2), parent.Location.Y + Math.Max(0, (parent.Height - Height) / 2));

		InitializeLayout();
		_clickAwayFilter = new ClickAwayFilter(_historyListBox, () => _activeSearchTextBox, HideHistoryList);
		Application.AddMessageFilter(_clickAwayFilter);
		Deactivate += (_, _) => HideHistoryList();
		FormClosing += AssetBrowser_FormClosing;
	}

	private void InitializeLayout()
	{
		_menuStrip.Items.AddRange([_fileMenu, _optionsMenu, _assetMenu, _miscMenu]);
		_fileMenu.DropDownItems.AddRange([_loadAssetMapMenuItem, _loadCabMapMenuItem, new ToolStripSeparator(), _resetMenuItem]);
		_assetMenu.DropDownItems.AddRange([_loadSelectedMenuItem, _appendSelectedMenuItem, new ToolStripSeparator(), _exportCabMenuItem]);
		_optionsMenu.DropDownItems.Add(_fastLoadMenuItem);
		_miscMenu.DropDownItems.AddRange([_convertAssetMapToJsonMenuItem, new ToolStripSeparator(), _clearSearchHistoryMenuItem]);

		_loadAssetMapMenuItem.Click += loadAssetMap_Click;
		_loadCabMapMenuItem.Click += loadCabMap_Click;
		_resetMenuItem.Click += reset_Click;
		_loadSelectedMenuItem.Click += loadSelected_Click;
		_appendSelectedMenuItem.Click += appendSelected_Click;
		_exportCabMenuItem.Click += exportCabMenuItem_Click;
		_convertAssetMapToJsonMenuItem.Click += convertAssetMapToJson_Click;
		_clearSearchHistoryMenuItem.Click += clearSearchHistory_Click;

		_filtersPanel.ColumnCount = 5;
		_filtersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
		_filtersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
		_filtersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));
		_filtersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
		_filtersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15F));
		_filtersPanel.Dock = DockStyle.Top;
		_filtersPanel.Padding = new Padding(1, 3, 1, 3);
		_filtersPanel.Height = 34;
		_filtersPanel.Controls.Add(_nameTextBox, 0, 0);
		_filtersPanel.Controls.Add(_containerTextBox, 1, 0);
		_filtersPanel.Controls.Add(_sourceTextBox, 2, 0);
		_filtersPanel.Controls.Add(_pathTextBox, 3, 0);
		_filtersPanel.Controls.Add(_typeTextBox, 4, 0);

		foreach (TextBox textBox in new[] { _nameTextBox, _containerTextBox, _sourceTextBox, _pathTextBox, _typeTextBox })
		{
			textBox.KeyDown += SearchTextBox_KeyDown;
			textBox.Enter += SearchTextBox_Enter;
			textBox.Leave += SearchTextBox_Leave;
			textBox.TextChanged += SearchTextBox_TextChanged;
			textBox.MouseDown += SearchTextBox_MouseDown;
			textBox.PreviewKeyDown += (_, e) =>
			{
				if (_historyListBox.Visible && (e.KeyCode == Keys.Tab || e.KeyCode == Keys.Down))
				{
					e.IsInputKey = true;
				}
			};
		}

		_assetListView.Columns.Add("Name", 400);
		_assetListView.Columns.Add("Container", 240);
		_assetListView.Columns.Add("Source", 180);
		_assetListView.Columns.Add("PathID", 100);
		_assetListView.Columns.Add("Type", 160);
		_assetListView.Dock = DockStyle.Fill;
		_assetListView.FullRowSelect = true;
		_assetListView.GridLines = true;
		_assetListView.HideSelection = false;
		_assetListView.MultiSelect = true;
		_assetListView.View = View.Details;
		_assetListView.VirtualMode = true;
		_assetListView.RetrieveVirtualItem += assetListView_RetrieveVirtualItem;
		_assetListView.ColumnClick += assetListView_ColumnClick;
		_assetListView.MouseClick += assetListView_MouseClick;
		_assetListView.KeyDown += assetListView_KeyDown;

		ToolStripMenuItem copyMenuItem = new("Copy text");
		ToolStripMenuItem contextLoadSelected = new("Load selected");
		ToolStripMenuItem contextAppendSelected = new("Load selected (append)");
		ToolStripMenuItem contextExportCab = new("Export CAB for selected assets");
		copyMenuItem.Click += copyMenuItem_Click;
		contextLoadSelected.Click += loadSelected_Click;
		contextAppendSelected.Click += appendSelected_Click;
		contextExportCab.Click += exportCabMenuItem_Click;
		_contextMenuStrip.Items.AddRange([copyMenuItem, new ToolStripSeparator(), contextLoadSelected, contextAppendSelected, new ToolStripSeparator(), contextExportCab]);

		_statusStrip.Items.Add(_statusLabel);

		Panel listPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(4, 0, 4, 0) };
		listPanel.Controls.Add(_assetListView);

		Controls.Add(listPanel);
		Controls.Add(_filtersPanel);
		Controls.Add(_menuStrip);
		Controls.Add(_statusStrip);
		MainMenuStrip = _menuStrip;
		_statusStrip.Dock = DockStyle.Bottom;
		_historyListBox.MouseDown += (_, _) => _historyListClicked = true;
		_historyListBox.Click += HistoryListBox_Click;
		_historyListBox.KeyDown += HistoryListBox_KeyDown;
		Controls.Add(_historyListBox);
		_historyListBox.BringToFront();
	}

	private async void loadAssetMap_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new() { Multiselect = false, Filter = "MessagePack AssetMap File|*.map" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		try
		{
			_loadAssetMapMenuItem.Enabled = false;
			_statusLabel.Text = "Loading AssetMap...";
			string mapPath = dialog.FileName;
			string cabPath = Path.ChangeExtension(mapPath, ".bin");
			await Task.Run(() => _service.LoadAssetMap(mapPath));
			if (File.Exists(cabPath))
			{
				await Task.Run(() => _service.CabMap.Load(cabPath));
			}
			ApplyLoadedEntries(_service.Entries);
			UpdateStatusBar();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Load AssetMap failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
			_statusLabel.Text = "Load AssetMap failed";
		}
		finally
		{
			_loadAssetMapMenuItem.Enabled = true;
		}
	}

	private async void loadCabMap_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new() { Multiselect = false, Filter = "CABMap File|*.bin" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		try
		{
			_loadCabMapMenuItem.Enabled = false;
			await Task.Run(() => _service.CabMap.Load(dialog.FileName));
			UpdateStatusBar();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Load CABMap failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			_loadCabMapMenuItem.Enabled = true;
		}
	}

	private void reset_Click(object? sender, EventArgs e)
	{
		_service.Clear();
		foreach (TextBox textBox in new[] { _nameTextBox, _containerTextBox, _sourceTextBox, _pathTextBox, _typeTextBox })
		{
			textBox.Clear();
		}
		_assetEntries.Clear();
		_totalCount = 0;
		_assetListView.VirtualListSize = 0;
		UpdateStatusBar();
	}

	private async void loadSelected_Click(object? sender, EventArgs e)
	{
		IReadOnlyList<AssetMapEntry> selected = GetSelectedEntries();
		if (selected.Count == 0)
		{
			return;
		}

		IReadOnlyList<string> files = _service.ResolveSelectedSourceFiles(selected);
		if (files.Count == 0)
		{
			MessageBox.Show(this, "No source files could be resolved from the selected AssetMap entries.", "Asset Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		await _parent.LoadAssetBrowserPathsAsync(files, append: false);
	}

	private async void appendSelected_Click(object? sender, EventArgs e)
	{
		IReadOnlyList<AssetMapEntry> selected = GetSelectedEntries();
		if (selected.Count == 0)
		{
			return;
		}

		IReadOnlyList<string> files = _service.ResolveSelectedSourceFiles(selected);
		if (files.Count == 0)
		{
			MessageBox.Show(this, "No source files could be resolved from the selected AssetMap entries.", "Asset Browser", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		await _parent.LoadAssetBrowserPathsAsync(files, append: true);
	}

	private async void exportCabMenuItem_Click(object? sender, EventArgs e)
	{
		IReadOnlyList<AssetMapEntry> selected = GetSelectedEntries();
		if (selected.Count == 0)
		{
			return;
		}

		if (!_service.CabMap.HasCabMap)
		{
			_parent.ShowCabMapRequiredDialog();
			return;
		}

		using FolderBrowserDialog dialog = new();
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		try
		{
			int totalExported = 0;
			foreach (AssetMapEntry entry in selected)
			{
				string? cabName = !string.IsNullOrWhiteSpace(entry.CAB)
					? entry.CAB
					: _service.ResolveSelectedCabNames([entry]).FirstOrDefault();
				if (string.IsNullOrWhiteSpace(cabName))
				{
					continue;
				}

				ExportCabResult result = await Task.Run(() => _service.CabMap.ExportCabs(cabName, entry.Name, dialog.SelectedPath, overwrite: true));
				totalExported += result.ExportedCount;
			}
			_statusLabel.Text = $"CAB export completed: {totalExported} file(s) exported.";
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Export CAB failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private async void convertAssetMapToJson_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new() { Filter = "AssetMap files (*.map)|*.map", Title = "Select AssetMap file" };
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		try
		{
			await Task.Run(() => _service.ConvertAssetMapToJson(dialog.FileName));
			_statusLabel.Text = $"Converted {Path.GetFileName(dialog.FileName)} to JSON.";
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Convert AssetMap failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private void clearSearchHistory_Click(object? sender, EventArgs e)
	{
		BrowserSearchHistory.ClearKeys(FilterFields);
		HideHistoryList();
	}

	private void ApplyLoadedEntries(IReadOnlyList<AssetMapEntry> entries)
	{
		_sortColumn = -1;
		_reverseSort = false;
		_assetEntries.Clear();
		_assetEntries.AddRange(entries);
		_totalCount = _assetEntries.Count;
		_assetListView.VirtualListSize = _assetEntries.Count;
		_assetListView.Refresh();
	}

	private void ApplyFilters()
	{
		try
		{
			IReadOnlyList<AssetMapEntry> filtered = _service.Filter(_filters);
			_assetEntries.Clear();
			_assetEntries.AddRange(filtered);
			_assetListView.VirtualListSize = 0;
			_assetListView.VirtualListSize = _assetEntries.Count;
			_assetListView.Refresh();
			UpdateStatusBar();
		}
		catch (RegexParseException)
		{
			_statusLabel.Text = "Invalid regex.";
		}
		catch (ArgumentException)
		{
			_statusLabel.Text = "Invalid regex.";
		}
	}

	private IReadOnlyList<AssetMapEntry> GetSelectedEntries()
	{
		List<AssetMapEntry> selected = [];
		foreach (int index in _assetListView.SelectedIndices)
		{
			if ((uint)index < (uint)_assetEntries.Count)
			{
				selected.Add(_assetEntries[index]);
			}
		}
		return selected;
	}

	private void assetListView_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
	{
		if ((uint)e.ItemIndex >= (uint)_assetEntries.Count)
		{
			return;
		}

		AssetMapEntry entry = _assetEntries[e.ItemIndex];
		ListViewItem item = new(entry.Name);
		item.SubItems.Add(entry.Container);
		item.SubItems.Add(entry.Source);
		item.SubItems.Add(entry.PathIDString);
		item.SubItems.Add(entry.TypeString);
		e.Item = item;
	}

	private void assetListView_ColumnClick(object? sender, ColumnClickEventArgs e)
	{
		_reverseSort = _sortColumn == e.Column && !_reverseSort;
		_sortColumn = e.Column;

		Comparison<AssetMapEntry> comparison = e.Column switch
		{
			0 => static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
			1 => static (a, b) => string.Compare(a.Container, b.Container, StringComparison.OrdinalIgnoreCase),
			2 => static (a, b) => string.Compare(a.Source, b.Source, StringComparison.OrdinalIgnoreCase),
			3 => static (a, b) => a.PathID.CompareTo(b.PathID),
			4 => static (a, b) => string.Compare(a.TypeString, b.TypeString, StringComparison.OrdinalIgnoreCase),
			_ => static (_, _) => 0
		};

		_assetEntries.Sort((a, b) => _reverseSort ? comparison(b, a) : comparison(a, b));
		_assetListView.Refresh();
	}

	private void assetListView_MouseClick(object? sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Right || _assetListView.SelectedIndices.Count == 0)
		{
			return;
		}

		ListViewHitTestInfo hit = _assetListView.HitTest(e.Location);
		_clickedColumnIndex = hit.Item is not null && hit.SubItem is not null ? hit.Item.SubItems.IndexOf(hit.SubItem) : 0;
		_contextMenuStrip.Show(_assetListView, e.Location);
	}

	private void assetListView_KeyDown(object? sender, KeyEventArgs e)
	{
		if (!e.Control || e.KeyCode != Keys.C || _assetListView.SelectedIndices.Count == 0)
		{
			return;
		}

		List<string> lines = [];
		foreach (int index in _assetListView.SelectedIndices)
		{
			if ((uint)index >= (uint)_assetEntries.Count)
			{
				continue;
			}

			AssetMapEntry entry = _assetEntries[index];
			lines.Add(string.Join('\t', [entry.Name, entry.Container, entry.Source, entry.PathIDString, entry.TypeString]));
		}

		if (lines.Count > 0)
		{
			Clipboard.SetDataObject(string.Join(Environment.NewLine, lines));
		}

		e.Handled = true;
		e.SuppressKeyPress = true;
	}

	private void copyMenuItem_Click(object? sender, EventArgs e)
	{
		List<string> lines = [];
		foreach (int index in _assetListView.SelectedIndices)
		{
			if ((uint)index >= (uint)_assetEntries.Count)
			{
				continue;
			}

			AssetMapEntry entry = _assetEntries[index];
			string value = _clickedColumnIndex switch
			{
				0 => entry.Name,
				1 => entry.Container,
				2 => entry.Source,
				3 => entry.PathIDString,
				4 => entry.TypeString,
				_ => entry.Name
			};
			lines.Add(value);
		}

		if (lines.Count > 0)
		{
			Clipboard.SetDataObject(string.Join(Environment.NewLine, lines));
		}
	}

	private void SearchTextBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if (sender is not TextBox textBox)
		{
			return;
		}

		if (e.KeyCode == Keys.Escape)
		{
			e.SuppressKeyPress = true;
			HideHistoryList();
			return;
		}

		if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Tab) && _historyListBox.Visible)
		{
			e.SuppressKeyPress = true;
			_historyListBox.Focus();
			if (_historyListBox.Items.Count > 0 && _historyListBox.SelectedIndex < 0)
			{
				_historyListBox.SelectedIndex = 0;
			}
			return;
		}

		if (e.KeyCode != Keys.Enter)
		{
			return;
		}

		e.SuppressKeyPress = true;
		HideHistoryList();
		string? fieldName = textBox.Tag as string;
		if (fieldName is null)
		{
			return;
		}

		_filters[fieldName] = textBox.Text;
		ApplyFilters();
		if (!string.IsNullOrWhiteSpace(textBox.Text))
		{
			BrowserSearchHistory.AddEntry(fieldName, textBox.Text);
		}
	}

	private void SearchTextBox_Enter(object? sender, EventArgs e)
	{
		if (sender is not TextBox textBox)
		{
			return;
		}

		_activeSearchTextBox = textBox;
		if (_suppressShowHistory)
		{
			_suppressShowHistory = false;
		}
		else
		{
			ShowHistoryList(textBox);
		}
	}

	private void SearchTextBox_MouseDown(object? sender, MouseEventArgs e)
	{
		if (sender is TextBox textBox && textBox.Focused && !_historyListBox.Visible)
		{
			ShowHistoryList(textBox);
		}
	}

	private void SearchTextBox_Leave(object? sender, EventArgs e)
	{
		TextBox? leftTextBox = sender as TextBox;
		BeginInvoke(() =>
		{
			if (_historyListClicked)
			{
				_historyListClicked = false;
				return;
			}

			if (_historyListBox.Focused || _activeSearchTextBox != leftTextBox)
			{
				return;
			}

			HideHistoryList();
		});
	}

	private void SearchTextBox_TextChanged(object? sender, EventArgs e)
	{
		if (_suppressTextChanged)
		{
			return;
		}

		if (sender is TextBox textBox)
		{
			ShowHistoryList(textBox);
		}
	}

	private void ShowHistoryList(TextBox textBox)
	{
		string? fieldName = textBox.Tag as string;
		IReadOnlyList<string> all = BrowserSearchHistory.GetEntries(fieldName);
		if (string.IsNullOrWhiteSpace(fieldName) || all.Count == 0)
		{
			HideHistoryList();
			return;
		}

		List<string> filtered = string.IsNullOrEmpty(textBox.Text)
			? all.ToList()
			: all.Where(x => x.Contains(textBox.Text, StringComparison.OrdinalIgnoreCase)).ToList();

		if (filtered.Count == 0)
		{
			HideHistoryList();
			return;
		}

		_historyListBox.Items.Clear();
		int maxItems = Math.Min(filtered.Count, 8);
		for (int i = 0; i < maxItems; i++)
		{
			_historyListBox.Items.Add(filtered[i]);
		}

		System.Drawing.Point pos = textBox.Parent?.PointToScreen(textBox.Location) ?? textBox.PointToScreen(System.Drawing.Point.Empty);
		pos = PointToClient(pos);
		int height = _historyListBox.ItemHeight * maxItems + 4;
		_historyListBox.SetBounds(pos.X, pos.Y + textBox.Height, textBox.Width, height);
		_historyListBox.Visible = true;
		_historyListBox.BringToFront();
	}

	private void HideHistoryList() => _historyListBox.Visible = false;

	private void HistoryListBox_Click(object? sender, EventArgs e)
	{
		if (_historyListBox.SelectedItem is string selected)
		{
			ApplyHistorySelection(selected);
		}
	}

	private void HistoryListBox_KeyDown(object? sender, KeyEventArgs e)
	{
		if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab) && _historyListBox.SelectedItem is string selected)
		{
			e.SuppressKeyPress = true;
			ApplyHistorySelection(selected);
		}
		else if (e.KeyCode == Keys.Escape)
		{
			e.SuppressKeyPress = true;
			HideHistoryList();
			_activeSearchTextBox?.Focus();
		}
	}

	private void ApplyHistorySelection(string selected)
	{
		if (_activeSearchTextBox is null)
		{
			return;
		}

		_suppressTextChanged = true;
		_activeSearchTextBox.Text = selected;
		_activeSearchTextBox.SelectionStart = selected.Length;
		_suppressTextChanged = false;
		HideHistoryList();
		_suppressShowHistory = true;
		_activeSearchTextBox.Focus();
	}

	private void UpdateStatusBar()
	{
		if (_totalCount == 0)
		{
			_statusLabel.Text = "Load an AssetMap from File menu";
			return;
		}

		string cabState = _service.CabMap.HasCabMap ? "CABMap loaded" : "CABMap missing";
		_statusLabel.Text = _assetEntries.Count == _totalCount
			? $"{_totalCount:N0} assets | {cabState}"
			: $"{_assetEntries.Count:N0} / {_totalCount:N0} assets | {cabState}";
	}

	private void AssetBrowser_FormClosing(object? sender, FormClosingEventArgs e)
	{
		Application.RemoveMessageFilter(_clickAwayFilter);
	}
}
