using System.Text.RegularExpressions;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI;

// CAB-map ("virtual file") side of the single Asset List. Each CAB is one row sharing the same virtual-mode
// list, search box, type filter, sort, multi-select and context menu as loaded assets — only the backing
// differs. Loading or exporting a selection resolves its dependency closure through the CAB map.
public partial class MainForm
{
	/// <summary>Switch the Asset List to show the loaded CAB map's virtual files (from the cached rows).</summary>
	private void EnterCabMapMode()
	{
		_listMode = AssetListMode.CabMap;
		_adapter.Reset();                  // the list now shows virtual files, not loaded assets
		_filteredAssets = [];
		_assetIndexByObjectKey.Clear();
		_nodeByObjectKey.Clear();
		sceneTreeView.Nodes.Clear();
		_sceneRoots.Clear();
		_sortColumn = -1;
		_sortAscending = true;
		listSearch.Clear();
		RebuildFilters();                  // type combo from CAB class ids
		ApplyFilter();                     // -> ApplyCabMapFilter populates _filteredCabRows
		RefreshTitle();
		tabControl1.SelectedTab = tabPage2;
	}

	private void ApplyCabMapFilter()
	{
		string query = listSearch.Text.Trim();
		string typeFilter = typeFilterComboBox.SelectedItem as string ?? "All";
		bool hasType = !string.IsNullOrWhiteSpace(typeFilter) && !string.Equals(typeFilter, "All", StringComparison.OrdinalIgnoreCase);

		Regex? regex = null;
		if (query.Length > 0)
		{
			try { regex = new Regex(query, RegexOptions.IgnoreCase); }
			catch (ArgumentException) { /* fall back to substring match below */ }
		}

		List<ExportCabMap.CabRow> rows = new();
		foreach (ExportCabMap.CabRow row in _allCabRows)
		{
			if (hasType && !CabHasType(row, typeFilter)) continue;
			if (query.Length > 0 && !CabRowMatches(row, query, regex)) continue;
			rows.Add(row);
		}
		SortCabRowsInPlace(rows);
		_filteredCabRows = rows;

		// VirtualListSize = 0 then count clears stale selection and forces a full redraw of the virtual rows.
		assetListView.VirtualListSize = 0;
		assetListView.VirtualListSize = _filteredCabRows.Count;
		UpdateSortIndicator();
		SetStatus($"Showing {_filteredCabRows.Count:N0} / {_allCabRows.Count:N0} virtual files.");
		ClearPreviewSurfaces();
		_currentPreviewItem = null;
		assetInfoLabel.Text = _exportMap.HasNames
			? "CAB-map virtual files. Select rows, then right-click to load or export with dependencies."
			: "No name index (.names) loaded — rows show CAB hashes. Build one with the CLI --build-name-index to search by name.";
	}

	private static bool CabRowMatches(ExportCabMap.CabRow row, string query, Regex? regex)
	{
		bool Match(string value) => regex is not null ? regex.IsMatch(value) : value.Contains(query, StringComparison.OrdinalIgnoreCase);

		if (Match(row.Cab) || Match(row.RelativePath) || Match(CabTypeNames(row)))
		{
			return true;
		}
		foreach (string path in row.ContainerPaths)
		{
			if (Match(path)) return true;
		}
		return false;
	}

	private static bool CabHasType(ExportCabMap.CabRow row, string typeName)
	{
		foreach (int id in row.ClassIds)
		{
			string name = Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : id.ToString();
			if (string.Equals(name, typeName, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	private IEnumerable<string> CabMapTypeNames()
	{
		HashSet<string> types = new(StringComparer.OrdinalIgnoreCase);
		foreach (ExportCabMap.CabRow row in _allCabRows)
		{
			foreach (int id in row.ClassIds)
			{
				if (id == (int)ClassIDType.AssetBundle) continue;
				types.Add(Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : id.ToString());
			}
		}
		return types.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase);
	}

	private void SortCabRowsInPlace(List<ExportCabMap.CabRow> rows)
	{
		if (_sortColumn < 0 || rows.Count <= 1)
		{
			return;
		}
		// Columns: 0 Name, 1 Container, 2 Type, 3 PathID (n/a), 4 Source, 5 Deps.
		Comparison<ExportCabMap.CabRow> comparison = _sortColumn switch
		{
			0 => static (a, b) => string.Compare(CabDisplayName(a), CabDisplayName(b), StringComparison.OrdinalIgnoreCase),
			1 => static (a, b) => string.Compare(FirstContainerPath(a), FirstContainerPath(b), StringComparison.OrdinalIgnoreCase),
			2 => static (a, b) => string.Compare(CabTypeNames(a), CabTypeNames(b), StringComparison.OrdinalIgnoreCase),
			4 => static (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase),
			5 => static (a, b) => a.DependencyCount.CompareTo(b.DependencyCount),
			_ => static (_, _) => 0,
		};
		rows.Sort(comparison);
		if (!_sortAscending)
		{
			rows.Reverse();
		}
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
			.Where(static id => id != (int)ClassIDType.AssetBundle)
			.Select(static id => Enum.IsDefined(typeof(ClassIDType), id) ? ((ClassIDType)id).ToString() : id.ToString())
			.DefaultIfEmpty("AssetBundle"));
	}

	// ── selection helpers (virtual mode → active backing) ─────────────────────────────────────────
	private List<RipperAssetEntry> SelectedAssetEntries()
	{
		List<RipperAssetEntry> result = [];
		if (_listMode != AssetListMode.Assets)
		{
			return result;
		}
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
		foreach (int index in assetListView.SelectedIndices)
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
		if (assetListView.SelectedIndices.Count <= nth)
		{
			return null;
		}
		int index = assetListView.SelectedIndices[nth];
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
