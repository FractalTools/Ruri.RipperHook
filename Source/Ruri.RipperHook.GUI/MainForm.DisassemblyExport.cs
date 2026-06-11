using AssetRipper.GUI.Web;
using AssetRipper.Import.Configuration;

namespace Ruri.RipperHook.GUI;

// "Export Disassembly" — decompile the entire IL2CPP/Mono codebase to readable .cs with native
// x86/ARM disassembly injected as comments, skipping every asset. Enables AR_DisassemblyExporter_
// (scripts-only + force-decompile-all) and AR_Il2CppMethodDump_ (the asm), forces
// ScriptContentLevel=Level2, and skips StreamingAssets (code lives in the metadata, not bundles).
public partial class MainForm
{
	private static readonly string[] DisassemblyExportHooks = { "AR_DisassemblyExporter_", "AR_Il2CppMethodDump_" };

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
