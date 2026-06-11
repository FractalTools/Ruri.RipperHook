using AssetRipper.Export.Configuration;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;
using Ruri.RipperHook.AR;
using Ruri.RipperHook.GUI.Components;
using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI;

// CABMap-aware exports. A loaded map lets us load ONLY the bundles that actually contain a target
// asset type (+ their dependencies) instead of reading the whole game into memory and filtering.
// File → Load/Build CABMap manages the map; the map-aware menu items (Export All Shaders, Export by
// Type, and the right-click "Export with dependencies") are enabled only while a map is loaded.
public partial class MainForm
{
	private readonly ExportCabMap _exportMap = new();

	private const int ClassIdShader = (int)ClassIDType.Shader;
	private const int ClassIdComputeShader = (int)ClassIDType.ComputeShader;

	// ── map load / build ────────────────────────────────────────────
	private void loadCabMapToolStripMenuItem_Click(object? sender, EventArgs e)
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

		try
		{
			_exportMap.Load(dialog.FileName);
			SetStatus(string.Format(RuriLocalization.CabMapLoaded, _exportMap.CabCount, _exportMap.MapPath));
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), RuriLocalization.MenuLoadCabMap, MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		UpdateCabMapState();
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
		if (!_exportMap.HasMap || assetListView.SelectedItems.Count == 0)
		{
			return;
		}

		// Each selected asset's source CAB → load that bundle + its full transitive dependency closure.
		HashSet<string> cabs = new(StringComparer.OrdinalIgnoreCase);
		foreach (AssetItem item in assetListView.SelectedItems.Cast<AssetItem>())
		{
			if (!string.IsNullOrWhiteSpace(item.Entry.SourceFile))
			{
				cabs.Add(item.Entry.SourceFile);
			}
		}

		string[] files = cabs.Count > 0 ? _exportMap.ResolveFilesByCabs(cabs) : [];
		if (files.Length == 0)
		{
			MessageBox.Show(this, RuriLocalization.WithDepsNoSource, RuriLocalization.WithDepsCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		if (!TryPickOutputFolder(out string output))
		{
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

		await RunFilteredExportAsync(files, output, Array.Empty<string>(), static () => { }, static () => { }, text);
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
