using AssetRipper.GUI.Web;
using AssetRipper.Import.Configuration;

namespace Ruri.RipperHook.GUI;

public partial class MainForm
{
	private static readonly string[] DisassemblyExportHooks = { "DisassemblyExporter", "Il2CppMethodDump" };

	private async void disassemblyExportFromFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		if (!TryPickGameAndOutput(out string gameFolder, out string outputFolder))
		{
			return;
		}

		ScriptContentLevel savedLevel = GameFileLoader.Settings.ImportSettings.ScriptContentLevel;
		bool savedIgnoreStreaming = GameFileLoader.Settings.ImportSettings.IgnoreStreamingAssets;

		FilteredExportText text = new(
			RuriLocalization.DisassemblyExportCaption,
			RuriLocalization.DisassemblyExportPreparing,
			RuriLocalization.DisassemblyExportLoading,
			RuriLocalization.DisassemblyExportExporting,
			RuriLocalization.DisassemblyExportDone,
			RuriLocalization.DisassemblyExportFailedCaption,
			RuriLocalization.DisassemblyExportFailedStatus);

		await RunFilteredExportAsync(
			new[] { gameFolder },
			outputFolder,
			DisassemblyExportHooks,
			applyOverrides: () =>
			{
				GameFileLoader.Settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;
				GameFileLoader.Settings.ImportSettings.IgnoreStreamingAssets = true;
			},
			restoreOverrides: () =>
			{
				GameFileLoader.Settings.ImportSettings.ScriptContentLevel = savedLevel;
				GameFileLoader.Settings.ImportSettings.IgnoreStreamingAssets = savedIgnoreStreaming;
			},
			text);
	}
}
