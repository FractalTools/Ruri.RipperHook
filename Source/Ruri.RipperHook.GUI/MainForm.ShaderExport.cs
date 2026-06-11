using AssetRipper.Export.Configuration;
using AssetRipper.GUI.Web;

namespace Ruri.RipperHook.GUI;

// "Export All Shaders" — export every Shader / ComputeShader, decompiled to readable code, and skip
// everything else. Enables AR_ShaderOnlyExport_ (filters the export to shader collections) and
// AR_ShaderDecompiler_ (swaps AR's dummy shader exporter for the real decompiler), and forces
// ShaderExportMode=Decompile. The selected game hook (e.g. EndField_1.2.4) provides the shader-binding
// hooks needed to correctly decompile EndField's Vulkan shaders. StreamingAssets are NOT skipped here —
// most game shaders live in the asset bundles, so they must be loaded to be exported.
public partial class MainForm
{
	private static readonly string[] ShaderExportHooks = { "AR_ShaderOnlyExport_", "AR_ShaderDecompiler_" };

	private async void shaderExportFromFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		if (!TryPickGameAndOutput(out string gameFolder, out string outputFolder))
		{
			return;
		}

		ShaderExportMode savedShaderMode = GameFileLoader.Settings.ExportSettings.ShaderExportMode;

		FilteredExportText text = new(
			RuriLocalization.ShaderExportCaption,
			RuriLocalization.ShaderExportPreparing,
			RuriLocalization.ShaderExportLoading,
			RuriLocalization.ShaderExportExporting,
			RuriLocalization.ShaderExportDone,
			RuriLocalization.ShaderExportFailedCaption,
			RuriLocalization.ShaderExportFailedStatus);

		await RunFilteredExportAsync(
			new[] { gameFolder },
			outputFolder,
			ShaderExportHooks,
			applyOverrides: () => GameFileLoader.Settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile,
			restoreOverrides: () => GameFileLoader.Settings.ExportSettings.ShaderExportMode = savedShaderMode,
			text);
	}
}
