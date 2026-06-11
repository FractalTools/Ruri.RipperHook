using AssetRipper.GUI.Web;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using Ruri.Hook.Config;

namespace Ruri.RipperHook.GUI;

// "Export Code Only" (仅导出代码) — decompiles the entire IL2CPP/Mono codebase and skips every
// asset. It forces, for the duration of one export and then restores:
//   * ScriptContentLevel = Level2          → full decompiled method bodies
//   * ImportSettings.IgnoreStreamingAssets → don't load the (often huge, encrypted) StreamingAssets;
//                                            the code comes from global-metadata + GameAssembly, not bundles
//   * AR_Il2CppMethodDump_                 → inject native x86/ARM asm as // comments in the scripts
//   * AR_CodeOnlyExport_                   → filter the project export down to script collections only
// The game hook that decrypts/loads the title (e.g. EndField_1.2.4, which also carries the
// global-metadata type-tree fix) must already be selected in the Hooks tree.
// All user-facing text comes from RuriLocalization — no hardcoded plaintext.
public partial class MainForm
{
	private const string CodeOnlyExportHookId = "AR_CodeOnlyExport_";
	private const string Il2CppMethodDumpHookIdForCode = "AR_Il2CppMethodDump_";

	private async void codeExportFromFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		string gameFolder;
		using (FolderBrowserDialog dialog = new()
		{
			Description = RuriLocalization.CodeExportSelectGameFolder,
			UseDescriptionForTitle = true,
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			gameFolder = dialog.SelectedPath;
		}

		string outputFolder;
		using (FolderBrowserDialog dialog = new()
		{
			Description = RuriLocalization.CodeExportSelectOutputFolder,
			UseDescriptionForTitle = true,
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			outputFolder = dialog.SelectedPath;
		}

		await RunCodeExportAsync(new[] { gameFolder }, outputFolder);
	}

	private async Task RunCodeExportAsync(IReadOnlyList<string> inputPaths, string outputPath)
	{
		if (inputPaths.Count == 0 || string.IsNullOrWhiteSpace(outputPath))
		{
			return;
		}

		// Never export onto the game itself or a parent of it.
		string fullOutput = Path.GetFullPath(outputPath);
		foreach (string input in inputPaths)
		{
			string fullInput = Path.GetFullPath(input);
			if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase)
				|| fullInput.StartsWith(fullOutput + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				MessageBox.Show(this, string.Format(RuriLocalization.CodeExportOutputInsideInput, fullOutput),
					RuriLocalization.CodeExportCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}
		}

		if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
		{
			DialogResult result = MessageBox.Show(
				this,
				string.Format(RuriLocalization.CodeExportOutputNonEmpty, outputPath),
				RuriLocalization.CodeExportCaption,
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Warning);
			if (result != DialogResult.Yes)
			{
				SetStatus(RuriLocalization.CodeExportCancelled);
				return;
			}
		}

		ResetLoadedSession();
		_adapter.Reset();
		ResetForm();

		// Snapshot everything we override so the GUI returns to the user's settings afterwards.
		HookConfig originalConfig = new()
		{
			EnabledHooks = new HashSet<string>(_hookConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase),
		};
		ScriptContentLevel savedLevel = GameFileLoader.Settings.ImportSettings.ScriptContentLevel;
		bool savedIgnoreStreaming = GameFileLoader.Settings.ImportSettings.IgnoreStreamingAssets;
		bool savedHeadless = GameFileLoader.Headless;

		bool hookSetChanged = !_hookConfig.EnabledHooks.Contains(CodeOnlyExportHookId)
			|| !_hookConfig.EnabledHooks.Contains(Il2CppMethodDumpHookIdForCode);

		ToggleUi(false);
		try
		{
			// 1) Turn on the two feature hooks: code-only filter + native asm injection.
			if (hookSetChanged)
			{
				HookConfig augmented = new()
				{
					EnabledHooks = new HashSet<string>(_hookConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase),
				};
				augmented.EnabledHooks.Add(CodeOnlyExportHookId);
				augmented.EnabledHooks.Add(Il2CppMethodDumpHookIdForCode);
				SetStatus(RuriLocalization.CodeExportPreparing);
				await ApplyHookConfigurationAsync(augmented, reloadCurrentPaths: false);
			}

			// 2) Full decompiled bodies; skip the heavy StreamingAssets (code lives in metadata).
			GameFileLoader.Settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level2;
			GameFileLoader.Settings.ImportSettings.IgnoreStreamingAssets = true;

			// Suppress AssetRipper's own overwrite confirmation — we already confirmed above.
			GameFileLoader.Headless = true;

			string[] pathArray = inputPaths.ToArray();
			string loadLabel = inputPaths.Count == 1 ? inputPaths[0] : $"{inputPaths.Count}";
			SetStatus(string.Format(RuriLocalization.CodeExportLoading, loadLabel));
			await Task.Run(() => GameFileLoader.LoadAndProcess(pathArray));

			SetStatus(string.Format(RuriLocalization.CodeExportExporting, outputPath));
			Logger.Info(LogCategory.Export, $"Code-only export -> {outputPath} (ScriptContentLevel=Level2, IgnoreStreamingAssets, {CodeOnlyExportHookId} + {Il2CppMethodDumpHookIdForCode} on)");

			await Task.Run(async () =>
			{
				await GameFileLoader.ExportUnityProject(outputPath);
			});

			SetStatus(string.Format(RuriLocalization.CodeExportDone, outputPath));
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), RuriLocalization.CodeExportFailedCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
			SetStatus(RuriLocalization.CodeExportFailedStatus);
		}
		finally
		{
			GameFileLoader.Settings.ImportSettings.ScriptContentLevel = savedLevel;
			GameFileLoader.Settings.ImportSettings.IgnoreStreamingAssets = savedIgnoreStreaming;
			GameFileLoader.Headless = savedHeadless;
			// Restore the user's hook set (removes the temporarily-added feature hooks).
			if (hookSetChanged)
			{
				try
				{
					await ApplyHookConfigurationAsync(originalConfig, reloadCurrentPaths: false);
				}
				catch (Exception ex)
				{
					Logger.Warning(LogCategory.Export, $"Code-only export: failed to restore hook config: {ex.Message}");
				}
			}

			// Whole-game GameData is huge; drop it so the GUI returns to baseline memory.
			_adapter.Reset();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			ToggleUi(true);
		}
	}
}
