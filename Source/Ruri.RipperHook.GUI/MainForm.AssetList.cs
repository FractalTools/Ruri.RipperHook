using AssetRipper.SourceGenerated.Classes.ClassID_1;
using Ruri.RipperHook.CabMapping;
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
		int rowId = (uint)index < (uint)_visibleCabIds.Length ? _visibleCabIds[index] : -1;
		Func<string, string> value = column => rowId >= 0 && _cabSearch is not null
			? column switch
			{
				"Name" => _cabSearch.Field(rowId, "name"),
				"Container" => _cabSearch.Field(rowId, "container"),
				"Type" => _cabSearch.Field(rowId, "type_names"),
				"Source" => _cabSearch.Field(rowId, "source"),
				"Deps" => _cabSearch.Field(rowId, "deps"),
				_ => string.Empty,
			}
			: string.Empty;
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
	// Column index -> the shared engine's field name (see CabTableSearch): 0 Name, 1 Container,
	// 2 Type, 3 Source, 4 Deps.
	private static readonly string[] VirtualColumnFields = ["name", "container", "type_names", "source", "deps"];

	/// <summary>Show the loaded CAB map's virtual files; leaves loaded assets and the loaded Asset List untouched.</summary>
	private void ShowVirtualRows()
	{
		_cabSearch = _exportMap.Table is { } table ? new CabTableSearch(table) : null;
		_virtualPreviewCache.Clear();
		_virtualSortColumn = 1;      // initial view: ascending by container path, as always
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
		if (_cabSearch is null)
		{
			_visibleCabIds = [];
			virtualListView.VirtualListSize = 0;
			return;
		}
		// The ONE search/rule/sort engine (CabTableSearch) -- the same call the Blender/Painter
		// bridge makes; this method owns no matching logic at all.
		string sortColumn = (uint)_virtualSortColumn < (uint)VirtualColumnFields.Length
			? VirtualColumnFields[_virtualSortColumn] : "name";
		int sortDirection = _virtualSortColumn < 0 ? 0 : (_virtualSortAscending ? 1 : 2);
		_visibleCabIds = _cabSearch.Search(virtualSearch.Text.Trim(), CabRulesForEngine(), sortColumn, sortDirection);

		// VirtualListSize = 0 then count clears stale selection and forces a full redraw of the virtual rows.
		virtualListView.VirtualListSize = 0;
		virtualListView.VirtualListSize = _visibleCabIds.Length;
		UpdateVirtualSortIndicator();
		if (_exportMap.CabCount > 0)
		{
			SetStatus($"Showing {_visibleCabIds.Length:N0} / {_exportMap.CabCount:N0} virtual files.");
		}
	}

	/// <summary>The shared rule list in the engine's shape. GUI column labels map to engine field
	/// names; PathID has no cabmap column and resolves to the empty string, exactly what the old
	/// per-row getter returned for it.</summary>
	private List<CabFilterRule> CabRulesForEngine()
	{
		List<CabFilterRule> rules = new(_filterRules.Count);
		foreach (FilterRule rule in _filterRules)
		{
			string field = rule.Column switch
			{
				"Name" => "name",
				"Container" => "container",
				"Type" => "type_names",
				"Source" => "source",
				"Deps" => "deps",
				_ => rule.Column.ToLowerInvariant(),
			};
			string relation = rule.Relation switch
			{
				FilterRelation.Is => "is",
				FilterRelation.IsNot => "is_not",
				FilterRelation.Contains => "contains",
				FilterRelation.Excludes => "excludes",
				FilterRelation.BeginsWith => "begins_with",
				FilterRelation.EndsWith => "ends_with",
				FilterRelation.LessThan => "less_than",
				FilterRelation.MoreThan => "more_than",
				FilterRelation.Matches => "matches_regex",
				FilterRelation.NotMatches => "not_matches_regex",
				_ => "contains",
			};
			rules.Add(new CabFilterRule(field, relation, rule.Value, rule.Include, rule.Enabled));
		}
		return rules;
	}

	private void virtualListView_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
	{
		if (_cabSearch is null || (uint)e.ItemIndex >= (uint)_visibleCabIds.Length)
		{
			return;
		}
		int id = _visibleCabIds[e.ItemIndex];
		string cab = _exportMap.Table!.CabName(id);
		string name = _cabSearch.Field(id, "name");
		string container = _cabSearch.Field(id, "container");
		ListViewItem item = new(name.Length > 0 ? name : cab);
		item.SubItems.Add(container.Length > 0 ? container : cab);
		item.SubItems.Add(_cabSearch.Field(id, "type_names"));
		item.SubItems.Add(_cabSearch.Field(id, "source"));   // Source = the hosting .chk
		item.SubItems.Add(_cabSearch.Field(id, "deps"));
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

	private IEnumerable<string> CabMapTypeNames()
	{
		HashSet<string> types = new(StringComparer.OrdinalIgnoreCase);
		foreach (int id in _exportMap.AvailableClassIds)
		{
			if (id == (int)AssetRipper.SourceGenerated.ClassIDType.AssetBundle) continue;
			types.Add(Enum.IsDefined(typeof(AssetRipper.SourceGenerated.ClassIDType), id) ? ((AssetRipper.SourceGenerated.ClassIDType)id).ToString() : id.ToString());
		}
		return types.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase);
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
		if (_exportMap.Table is not { } table)
		{
			return cabs;
		}
		foreach (int index in virtualListView.SelectedIndices)
		{
			if ((uint)index < (uint)_visibleCabIds.Length)
			{
				cabs.Add(table.CabName(_visibleCabIds[index]));
			}
		}
		return cabs;
	}

	private ExportCabMap.CabRow? CabRowAtSelection(int nth)
	{
		if (virtualListView.SelectedIndices.Count <= nth || !_exportMap.HasMap)
		{
			return null;
		}
		int index = virtualListView.SelectedIndices[nth];
		return (uint)index < (uint)_visibleCabIds.Length ? _exportMap.RowAt(_visibleCabIds[index]) : null;
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
