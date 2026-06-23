using AssetRipper.SourceGenerated.Classes.ClassID_1;
using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI;

// The "Virtual Asset List" tab — one row per CAB from the loaded CAB map (258k+), decoupled from the loaded
// "Asset List": searching/filtering/sorting here never disturbs loaded assets. Selecting a row previews it on
// demand (bundle-granular). Right-click loads the selection's dependency closure INTO the Asset List (Append by
// default so successive loads accumulate, or Reset) or exports it with all dependencies. The tab + list +
// context menu are built in code and inserted right after the loaded Asset List tab.
public partial class MainForm
{
	private TabPage tabPageVirtual = null!;
	private ListView virtualListView = null!;
	private TextBox virtualSearch = null!;
	private ContextMenuStrip virtualContextMenu = null!;
	private ToolStripMenuItem virtualLoadAppendMenuItem = null!;
	private ToolStripMenuItem virtualLoadResetMenuItem = null!;
	private ToolStripMenuItem virtualExportWithDepsMenuItem = null!;
	private ToolStripMenuItem virtualQuickInclude = null!;
	private ToolStripMenuItem virtualQuickExclude = null!;
	private System.Windows.Forms.Timer _virtualSearchTimer = null!;
	private int _virtualSortColumn = -1;
	private bool _virtualSortAscending = true;

	// ── tab / list / context menu construction ──────────────────────────────────────────────────────
	private void BuildVirtualTab()
	{
		_virtualSearchTimer = new System.Windows.Forms.Timer(components) { Interval = 250 };
		_virtualSearchTimer.Tick += (_, _) => { _virtualSearchTimer.Stop(); ApplyVirtualFilter(); };

		virtualSearch = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search virtual files (Name / Container / Source / Type)…" };
		virtualSearch.TextChanged += (_, _) => { _virtualSearchTimer.Stop(); _virtualSearchTimer.Start(); };

		virtualListView = new ListView
		{
			Dock = DockStyle.Fill,
			View = View.Details,
			FullRowSelect = true,
			HideSelection = false,
			MultiSelect = true,
			VirtualMode = true,
			UseCompatibleStateImageBehavior = false,
		};
		virtualListView.Columns.Add("Name", 240);
		virtualListView.Columns.Add("Container", 320);
		virtualListView.Columns.Add("Type", 150);
		virtualListView.Columns.Add("Source", 200);
		virtualListView.Columns.Add("Deps", 50);
		virtualListView.RetrieveVirtualItem += virtualListView_RetrieveVirtualItem;
		virtualListView.SelectedIndexChanged += virtualListView_SelectedIndexChanged;
		virtualListView.ColumnClick += virtualListView_ColumnClick;
		virtualListView.MouseUp += assetListView_MouseUp;   // shared sender-aware right-click selection

		BuildVirtualContextMenu();
		virtualListView.ContextMenuStrip = virtualContextMenu;

		tabPageVirtual = new TabPage("Virtual Asset List") { UseVisualStyleBackColor = true };
		tabPageVirtual.Controls.Add(virtualListView);
		tabPageVirtual.Controls.Add(virtualSearch);

		int assetTabIndex = tabControl1.TabPages.IndexOf(tabPage2);
		tabControl1.TabPages.Insert(assetTabIndex + 1, tabPageVirtual);

		tabControl1.SelectedIndexChanged += tabControl1_SelectedIndexChanged;   // lazy loaded-list refresh
	}

	private void BuildVirtualContextMenu()
	{
		virtualContextMenu = new ContextMenuStrip(components);
		virtualQuickInclude = new ToolStripMenuItem("Include");
		virtualQuickExclude = new ToolStripMenuItem("Exclude");
		virtualLoadAppendMenuItem = new ToolStripMenuItem("Load selected (append)", null, (_, _) => LoadSelectedVirtual(append: true));
		virtualLoadResetMenuItem = new ToolStripMenuItem("Load selected (reset)", null, (_, _) => LoadSelectedVirtual(append: false));
		virtualExportWithDepsMenuItem = new ToolStripMenuItem(RuriLocalization.ContextExportWithDeps, null, virtualExportWithDeps_Click);
		virtualContextMenu.Items.AddRange([
			virtualQuickInclude, virtualQuickExclude, new ToolStripSeparator(),
			virtualLoadAppendMenuItem, virtualLoadResetMenuItem, new ToolStripSeparator(),
			virtualExportWithDepsMenuItem,
		]);
		virtualContextMenu.Opening += virtualContextMenu_Opening;
	}

	private void virtualContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (virtualListView.SelectedIndices.Count == 0)
		{
			e.Cancel = true;
			return;
		}
		virtualExportWithDepsMenuItem.Enabled = _exportMap.HasMap;
		int index = virtualListView.SelectedIndices[0];
		Func<string, string> value = column => (uint)index < (uint)_filteredCabRows.Count ? CabColumnValue(_filteredCabRows[index], column) : string.Empty;
		PopulateQuickFilterMenu(virtualQuickInclude, virtualQuickExclude, value);
	}

	private async void LoadSelectedVirtual(bool append)
	{
		List<string> cabs = SelectedCabNames();
		if (cabs.Count > 0)
		{
			await LoadCabsScopedAsync(cabs, append);
		}
	}

	private async void virtualExportWithDeps_Click(object? sender, EventArgs e)
	{
		if (!_exportMap.HasMap)
		{
			return;
		}
		List<string> cabs = SelectedCabNames();
		if (cabs.Count == 0)
		{
			MessageBox.Show(this, RuriLocalization.WithDepsNoSource, RuriLocalization.WithDepsCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		if (!TryPickOutputFolder(out string output))
		{
			return;
		}
		await ExportCabsWithDepsAsync(cabs, output);
	}

	// ── populate / filter / sort ────────────────────────────────────────────────────────────────────
	/// <summary>Show the loaded CAB map's virtual files; leaves loaded assets and the loaded Asset List untouched.</summary>
	private void ShowVirtualRows(List<ExportCabMap.CabRow> rows)
	{
		_allCabRows = rows;
		_virtualPreviewCache.Clear();
		_virtualSortColumn = -1;
		_virtualSortAscending = true;
		virtualSearch.Clear();
		RebuildTypeList();
		ApplyVirtualFilter();
		tabControl1.SelectedTab = tabPageVirtual;
	}

	private void ApplyVirtualFilter()
	{
		if (virtualListView is null)
		{
			return;
		}
		string quick = virtualSearch.Text.Trim();
		List<ExportCabMap.CabRow> rows = _allCabRows.Where(r => RowPasses(quick, column => CabColumnValue(r, column))).ToList();
		SortCabRowsInPlace(rows);
		_filteredCabRows = rows;

		// VirtualListSize = 0 then count clears stale selection and forces a full redraw of the virtual rows.
		virtualListView.VirtualListSize = 0;
		virtualListView.VirtualListSize = _filteredCabRows.Count;
		UpdateVirtualSortIndicator();
		if (_allCabRows.Count > 0)
		{
			SetStatus($"Showing {_filteredCabRows.Count:N0} / {_allCabRows.Count:N0} virtual files.");
		}
	}

	private void virtualListView_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
	{
		if ((uint)e.ItemIndex >= (uint)_filteredCabRows.Count)
		{
			return;
		}
		ExportCabMap.CabRow row = _filteredCabRows[e.ItemIndex];
		ListViewItem item = new(CabDisplayName(row));
		item.SubItems.Add(row.ContainerPaths.Count > 0 ? string.Join("  |  ", row.ContainerPaths) : row.Cab);
		item.SubItems.Add(CabTypeNames(row));
		item.SubItems.Add(row.RelativePath);              // Source = the hosting .chk
		item.SubItems.Add(row.DependencyCount.ToString());
		e.Item = item;
	}

	private void virtualListView_SelectedIndexChanged(object? sender, EventArgs e)
	{
		StopAudio();
		int count = virtualListView.SelectedIndices.Count;
		if (count == 0)
		{
			ShowEmptyPreview();
			return;
		}

		// Show the CAB summary immediately, then load+render the real asset on demand (async).
		_currentPreviewItem = null;
		_previewRequestVersion++;
		ClearPreviewSurfaces();
		ExportCabMap.CabRow? single = count == 1 ? CabRowAtSelection(0) : null;
		assetInfoLabel.Text = single is not null
			? $"CAB: {single.Cab}\r\nSource: {single.RelativePath}\r\nDependencies: {single.DependencyCount}\r\n\r\n{string.Join("\r\n", single.ContainerPaths)}"
			: $"{count} virtual files selected. Right-click to load them (append/reset) or export with dependencies.";
		yamlTextBox.Text = "YAML is not available for CAB-map virtual files.";
		if (single is not null)
		{
			PreviewVirtualFileAsync(single);
		}
	}

	private void virtualListView_ColumnClick(object? sender, ColumnClickEventArgs e)
	{
		// Tri-state: asc → desc → unsorted (back to load order).
		if (e.Column == _virtualSortColumn)
		{
			if (_virtualSortAscending)
			{
				_virtualSortAscending = false;
			}
			else
			{
				_virtualSortColumn = -1;
				_virtualSortAscending = true;
			}
		}
		else
		{
			_virtualSortColumn = e.Column;
			_virtualSortAscending = true;
		}
		ApplyVirtualFilter();
	}

	private void UpdateVirtualSortIndicator()
	{
		for (int i = 0; i < virtualListView.Columns.Count; i++)
		{
			ColumnHeader col = virtualListView.Columns[i];
			string baseText = col.Text.TrimEnd(' ', '▲', '▼');
			col.Text = i == _virtualSortColumn
				? baseText + " " + (_virtualSortAscending ? '▲' : '▼')
				: baseText;
		}
	}

	private void SortCabRowsInPlace(List<ExportCabMap.CabRow> rows)
	{
		if (_virtualSortColumn < 0 || rows.Count <= 1)
		{
			return;
		}
		// Columns: 0 Name, 1 Container, 2 Type, 3 Source, 4 Deps.
		Comparison<ExportCabMap.CabRow> comparison = _virtualSortColumn switch
		{
			0 => static (a, b) => string.Compare(CabDisplayName(a), CabDisplayName(b), StringComparison.OrdinalIgnoreCase),
			1 => static (a, b) => string.Compare(FirstContainerPath(a), FirstContainerPath(b), StringComparison.OrdinalIgnoreCase),
			2 => static (a, b) => string.Compare(CabTypeNames(a), CabTypeNames(b), StringComparison.OrdinalIgnoreCase),
			3 => static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase),
			4 => static (a, b) => a.DependencyCount.CompareTo(b.DependencyCount),
			_ => static (_, _) => 0,
		};
		rows.Sort(comparison);
		if (!_virtualSortAscending)
		{
			rows.Reverse();
		}
	}

	private IEnumerable<string> CabMapTypeNames()
	{
		HashSet<string> types = new(StringComparer.OrdinalIgnoreCase);
		foreach (ExportCabMap.CabRow row in _allCabRows)
		{
			foreach (int id in row.ClassIds)
			{
				if (id == (int)AssetRipper.SourceGenerated.ClassIDType.AssetBundle) continue;
				types.Add(Enum.IsDefined(typeof(AssetRipper.SourceGenerated.ClassIDType), id) ? ((AssetRipper.SourceGenerated.ClassIDType)id).ToString() : id.ToString());
			}
		}
		return types.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase);
	}

	private static string FirstContainerPath(ExportCabMap.CabRow row) => row.ContainerPaths.Count > 0 ? row.ContainerPaths[0] : row.Cab;

	private static string CabDisplayName(ExportCabMap.CabRow row)
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

	private static string CabTypeNames(ExportCabMap.CabRow row)
	{
		return string.Join(", ", row.ClassIds
			.Where(static id => id != (int)AssetRipper.SourceGenerated.ClassIDType.AssetBundle)
			.Select(static id => Enum.IsDefined(typeof(AssetRipper.SourceGenerated.ClassIDType), id) ? ((AssetRipper.SourceGenerated.ClassIDType)id).ToString() : id.ToString())
			.DefaultIfEmpty("AssetBundle"));
	}

	// ── selection helpers ───────────────────────────────────────────────────────────────────────────
	private List<RipperAssetEntry> SelectedAssetEntries()
	{
		List<RipperAssetEntry> result = [];
		foreach (int index in assetListView.SelectedIndices)
		{
			if ((uint)index < (uint)_filteredAssets.Count)
			{
				result.Add(_filteredAssets[index]);
			}
		}
		return result;
	}

	private List<string> SelectedCabNames()
	{
		List<string> cabs = [];
		foreach (int index in virtualListView.SelectedIndices)
		{
			if ((uint)index < (uint)_filteredCabRows.Count)
			{
				cabs.Add(_filteredCabRows[index].Cab);
			}
		}
		return cabs;
	}

	private ExportCabMap.CabRow? CabRowAtSelection(int nth)
	{
		if (virtualListView.SelectedIndices.Count <= nth)
		{
			return null;
		}
		int index = virtualListView.SelectedIndices[nth];
		return (uint)index < (uint)_filteredCabRows.Count ? _filteredCabRows[index] : null;
	}

	private static string GetObjectKey(IGameObject gameObject)
	{
		return gameObject.Collection.Name + "|" + gameObject.PathID.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private void ClearPreviewSurfaces()
	{
		ClearMeshPreview();
		imagePreviewBox.Image?.Dispose();
		imagePreviewBox.Image = null;
		imagePreviewBox.Visible = false;
		glControl.Visible = false;
		textPreviewBox.Visible = false;
		textPreviewBox.Clear();
		audioPanel.Visible = false;
	}
}
