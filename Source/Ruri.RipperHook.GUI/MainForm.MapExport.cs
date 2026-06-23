using AssetRipper.Export.Configuration;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;
using Ruri.RipperHook.AR;
using Ruri.RipperHook.GUI.Components;
using Ruri.RipperHook.GUI.Services;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.GUI;

// CABMap-aware exports. A loaded map lets us load ONLY the bundles that actually contain a target
// asset type (+ their dependencies) instead of reading the whole game into memory and filtering.
// File → Load/Build CABMap manages the map; the map-aware menu items (Export All Shaders, Export by
// Type, and the right-click "Export with dependencies") are enabled only while a map is loaded.
public partial class MainForm
{
	private readonly ExportCabMap _exportMap = new();

	// Accumulated bundle-granular load filter (chunk-entry file names) across appended scoped loads, so a
	// reloaded old+new path set keeps every previously-loaded closure's bundles instead of filtering them out.
	private readonly HashSet<string> _scopedLoadFilter = new(StringComparer.OrdinalIgnoreCase);

	private const int ClassIdShader = (int)ClassIDType.Shader;
	private const int ClassIdComputeShader = (int)ClassIDType.ComputeShader;

	// ── map load / build ────────────────────────────────────────────
	private async void loadCabMapToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new()
		{
			Title = RuriLocalization.MenuLoadCabMap,
			Filter = "CABMap|*.cabmap;*.bin|All files|*.*",
			CheckFileExists = true,
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		string file = dialog.FileName;
		ToggleUi(false);
		try
		{
			// Load the map and, if present, its name-index sidecar (so the list is searchable by name), then
			// materialise + sort the virtual-file rows — all off the UI thread (258k+ CABs).
			List<ExportCabMap.CabRow> rows = [];
			await Task.Run(() =>
			{
				_exportMap.Load(file);
				string namesPath = ExportCabMap.NameIndexPath(file);
				if (File.Exists(namesPath))
				{
					_exportMap.LoadNames(namesPath);
				}
				rows = _exportMap.EnumerateCabRows()
					.OrderBy(static r => r.ContainerPaths.Count > 0 ? r.ContainerPaths[0] : r.Cab, StringComparer.OrdinalIgnoreCase)
					.ToList();
			});
			string nameState = _exportMap.HasNames ? RuriLocalization.CabMapNamesLoaded : RuriLocalization.CabMapNamesMissing;
			SetStatus(string.Format(RuriLocalization.CabMapLoaded, _exportMap.CabCount, _exportMap.MapPath) + " " + nameState);
			_allCabRows = rows;
			EnterCabMapMode();   // show the virtual files right in the Asset List
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), RuriLocalization.MenuLoadCabMap, MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			ToggleUi(true);
			UpdateCabMapState();
		}
	}

	/// <summary>
	/// On-demand, bundle-granular load of the selected CABs (+ their dependency closure) into the Asset List
	/// for preview. Only the closure's bundles are extracted from each chunk, so this stays memory-bounded
	/// even when a selection's chunks hold 100k+ unrelated bundles. Switches the list to loaded-asset mode.
	/// </summary>
	internal async Task LoadCabsScopedAsync(IReadOnlyList<string> seedCabs, bool append)
	{
		(string[] files, HashSet<string> fileNames) = _exportMap.ResolveScopedClosure(seedCabs);
		if (files.Length == 0)
		{
			SetStatus(RuriLocalization.WithDepsNoSource);
			return;
		}

		if (!append)
		{
			_scopedLoadFilter.Clear();
		}
		foreach (string fileName in fileNames)
		{
			_scopedLoadFilter.Add(fileName);
		}

		string[] nextPaths = append
			? _lastLoadedPaths.Concat(files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
			: files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

		// Drop the CAB-map search term so the loaded closure shows in full (not filtered to the search).
		listSearch.Clear();

		GameBundleHook.LoadIncludeFile = _scopedLoadFilter.Count > 0 ? name => _scopedLoadFilter.Contains(name) : null;
		try
		{
			await LoadPathsAsync(nextPaths, LoadSessionKind.MixedPaths, replaceCurrent: true);
		}
		finally
		{
			GameBundleHook.LoadIncludeFile = null;
		}
	}

	/// <summary>
	/// Unitypackage-style export of the selected CABs plus their full transitive dependency closure: load
	/// just those bundles (bundle-granular) then run a real AssetRipper export — models, prefabs, meshes,
	/// animations, textures, materials and everything they reference. Returns to CAB-map browsing afterwards.
	/// </summary>
	internal async Task ExportCabsWithDepsAsync(IReadOnlyList<string> seedCabs, string outputDir)
	{
		(string[] files, HashSet<string> fileNames) = _exportMap.ResolveScopedClosure(seedCabs);
		if (files.Length == 0)
		{
			MessageBox.Show(this, RuriLocalization.WithDepsNoSource, RuriLocalization.WithDepsCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		FilteredExportText text = new(
			RuriLocalization.WithDepsCaption,
			RuriLocalization.WithDepsPreparing,
			RuriLocalization.WithDepsLoading,
			RuriLocalization.WithDepsExporting,
			RuriLocalization.WithDepsDone,
			RuriLocalization.WithDepsFailedCaption,
			RuriLocalization.WithDepsFailedStatus);

		bool wasCabMap = _listMode == AssetListMode.CabMap;
		GameBundleHook.LoadIncludeFile = fileNames.Count > 0 ? name => fileNames.Contains(name) : null;
		try
		{
			await RunFilteredExportAsync(files, outputDir, Array.Empty<string>(), static () => { }, static () => { }, text);
		}
		finally
		{
			GameBundleHook.LoadIncludeFile = null;
		}

		// RunFilteredExportAsync resets the loaded session; restore the CAB-map view so browsing continues.
		if (wasCabMap && _allCabRows.Count > 0)
		{
			EnterCabMapMode();
		}
	}

	// ── unified context-menu actions (work in both list modes) ───────────────────────────────────
	private async void contextLoadSelectedMenuItem_Click(object? sender, EventArgs e)
	{
		if (_listMode != AssetListMode.CabMap)
		{
			return;
		}
		List<string> cabs = SelectedCabNames();
		if (cabs.Count > 0)
		{
			await LoadCabsScopedAsync(cabs, append: false);
		}
	}

	/// <summary>The CABs to export-with-dependencies: the selected virtual files, or the selected assets' CABs.</summary>
	private List<string> SelectedCabsForDependencyExport()
	{
		if (_listMode == AssetListMode.CabMap)
		{
			return SelectedCabNames();
		}
		HashSet<string> cabs = new(StringComparer.OrdinalIgnoreCase);
		foreach (RipperAssetEntry entry in SelectedAssetEntries())
		{
			if (!string.IsNullOrWhiteSpace(entry.SourceFile))
			{
				cabs.Add(entry.SourceFile);
			}
		}
		return cabs.ToList();
	}

	// Show only the actions that apply to the current list mode (Load selected is CAB-map only; the per-asset
	// Converted/YAML export is loaded-assets only; Export with dependencies works in both when a map is loaded).
	private void assetListContextMenuStrip_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		if (assetListView.SelectedIndices.Count == 0)
		{
			e.Cancel = true;
			return;
		}
		bool cabMap = _listMode == AssetListMode.CabMap;
		contextLoadSelectedMenuItem.Visible = cabMap;
		contextLoadSeparator.Visible = cabMap;
		contextExportSelectedAssetsMenuItem.Visible = !cabMap;
		contextExportWithDepsMenuItem.Enabled = _exportMap.HasMap;
	}

	private async void buildCabMapToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		string gameFolder;
		using (FolderBrowserDialog dialog = new()
		{
			Description = RuriLocalization.CabMapBuildSelectGameFolder,
			UseDescriptionForTitle = true,
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK) return;
			gameFolder = dialog.SelectedPath;
		}

		string outPath;
		using (SaveFileDialog dialog = new()
		{
			Title = RuriLocalization.MenuBuildCabMap,
			Filter = "CABMap|*.cabmap",
			FileName = "game.cabmap",
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK) return;
			outPath = dialog.FileName;
		}

		ToggleUi(false);
		SetStatus(string.Format(RuriLocalization.CabMapBuilding, gameFolder));
		try
		{
			int cabs = await Task.Run(() => ExportCabMap.Build(gameFolder, outPath));
			_exportMap.Load(outPath);
			SetStatus(string.Format(RuriLocalization.CabMapBuilt, cabs, outPath));
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), RuriLocalization.MenuBuildCabMap, MessageBoxButtons.OK, MessageBoxIcon.Error);
			SetStatus(RuriLocalization.CabMapBuildFailed);
		}
		finally
		{
			GameFileLoader.Reset();
			ToggleUi(true);
			UpdateCabMapState();
		}
	}

	/// <summary>Title-bar map indicator + enable/disable of the map-aware menu items.</summary>
	private void UpdateCabMapState()
	{
		RefreshTitle();
		shaderExportFromFolderToolStripMenuItem.Enabled = _exportMap.HasMap;
		byTypeExportToolStripMenuItem.Enabled = _exportMap.HasMap;
		contextExportWithDepsMenuItem.Enabled = _exportMap.HasMap;
	}

	/// <summary>Window title = app name + CABMap state + loaded-asset count. Call after either changes.</summary>
	private void RefreshTitle()
	{
		string map = _exportMap.HasMap
			? string.Format(RuriLocalization.TitleMapLoaded, _exportMap.CabCount)
			: RuriLocalization.TitleMapNone;
		string assets = _adapter.IsLoaded ? $" - {_adapter.Assets.Count} assets" : string.Empty;
		Text = $"RuriAssetRipper - {map}{assets}";
	}

	// ── map-aware exports ───────────────────────────────────────────
	private async void shaderExportFromFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		await RunMapTypeExportAsync(
			new HashSet<int> { ClassIdShader, ClassIdComputeShader },
			decompileShaders: true,
			new FilteredExportText(
				RuriLocalization.ShaderExportCaption,
				RuriLocalization.ShaderExportPreparing,
				RuriLocalization.ShaderExportLoading,
				RuriLocalization.ShaderExportExporting,
				RuriLocalization.ShaderExportDone,
				RuriLocalization.ShaderExportFailedCaption,
				RuriLocalization.ShaderExportFailedStatus));
	}

	private async void byTypeExportToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		if (!_exportMap.HasMap)
		{
			return;
		}

		HashSet<int> selected;
		using (TypePickerDialog dialog = new(_exportMap.AvailableClassIds))
		{
			if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedClassIds.Count == 0)
			{
				return;
			}
			selected = dialog.SelectedClassIds;
		}

		await RunMapTypeExportAsync(
			selected,
			decompileShaders: selected.Contains(ClassIdShader) || selected.Contains(ClassIdComputeShader),
			new FilteredExportText(
				RuriLocalization.ByTypeExportCaption,
				RuriLocalization.ByTypeExportPreparing,
				RuriLocalization.ByTypeExportLoading,
				RuriLocalization.ByTypeExportExporting,
				RuriLocalization.ByTypeExportDone,
				RuriLocalization.ByTypeExportFailedCaption,
				RuriLocalization.ByTypeExportFailedStatus));
	}

	private async void contextExportWithDepsMenuItem_Click(object? sender, EventArgs e)
	{
		if (!_exportMap.HasMap || assetListView.SelectedIndices.Count == 0)
		{
			return;
		}

		// Selected virtual files (CAB-map mode) or the selected assets' source CABs (Assets mode) → export
		// each bundle + its full transitive dependency closure, bundle-granular.
		List<string> cabs = SelectedCabsForDependencyExport();
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

	/// <summary>Resolve bundles for the types via the map, pick output, then export with the type filter applied.</summary>
	private async Task RunMapTypeExportAsync(HashSet<int> typeIds, bool decompileShaders, FilteredExportText text)
	{
		if (!_exportMap.HasMap || typeIds.Count == 0)
		{
			return;
		}

		string[] files = _exportMap.ResolveFilesByTypes(typeIds);
		if (files.Length == 0)
		{
			MessageBox.Show(this, RuriLocalization.NoBundlesForTypes, text.Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		if (!TryPickOutputFolder(out string output))
		{
			return;
		}

		string[] extraHooks = decompileShaders
			? new[] { "AR_TypeFilterExport_", "AR_ShaderDecompiler_" }
			: new[] { "AR_TypeFilterExport_" };

		ShaderExportMode savedShaderMode = GameFileLoader.Settings.ExportSettings.ShaderExportMode;

		await RunFilteredExportAsync(
			files,
			output,
			extraHooks,
			applyOverrides: () =>
			{
				AR_TypeFilterExport_Hook.TargetClassIds.Clear();
				foreach (int id in typeIds) AR_TypeFilterExport_Hook.TargetClassIds.Add(id);
				if (decompileShaders)
				{
					GameFileLoader.Settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
				}
				Logger.Info(LogCategory.Export, $"Map-type export: {files.Length} bundle(s), types [{string.Join(",", typeIds)}]");
			},
			restoreOverrides: () =>
			{
				AR_TypeFilterExport_Hook.TargetClassIds.Clear();
				GameFileLoader.Settings.ExportSettings.ShaderExportMode = savedShaderMode;
			},
			text);
	}
}
