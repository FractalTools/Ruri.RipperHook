using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using Ruri.Hook.Config;

namespace Ruri.RipperHook.GUI;

// Shared plumbing for the "export only X" menu features (Export Disassembly, Export All Shaders).
// Each feature: pick a game folder + output folder, temporarily enable some feature hooks and tweak
// a few settings, load + export, then restore everything. The chosen game hook (e.g. EndField_1.2.4)
// must already be selected in the Hooks tree so the title actually loads. All text comes from
// RuriLocalization — no hardcoded plaintext.
public partial class MainForm
{
	// Localized status/caption strings for one filtered-export flavour.
	private readonly record struct FilteredExportText(
		string Caption, string Preparing, string Loading, string Exporting, string Done,
		string FailedCaption, string FailedStatus);

	// Two folder pickers: game root, then output dir. Returns false if the user cancels either.
	private bool TryPickGameAndOutput(out string gameFolder, out string outputFolder)
	{
		gameFolder = string.Empty;
		outputFolder = string.Empty;

		using (FolderBrowserDialog dialog = new()
		{
			Description = RuriLocalization.ExportSelectGameFolder,
			UseDescriptionForTitle = true,
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return false;
			}
			gameFolder = dialog.SelectedPath;
		}

		using (FolderBrowserDialog dialog = new()
		{
			Description = RuriLocalization.ExportSelectOutputFolder,
			UseDescriptionForTitle = true,
		})
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return false;
			}
			outputFolder = dialog.SelectedPath;
		}
		return true;
	}

	/// <param name="extraHooks">Feature hook ids to enable for this export only (restored afterwards).</param>
	/// <param name="applyOverrides">Mutate GameFileLoader.Settings before load (e.g. ScriptContentLevel).</param>
	/// <param name="restoreOverrides">Undo <paramref name="applyOverrides"/> in the finally block.</param>
	private async Task RunFilteredExportAsync(
		IReadOnlyList<string> inputPaths,
		string outputPath,
		IReadOnlyCollection<string> extraHooks,
		Action applyOverrides,
		Action restoreOverrides,
		FilteredExportText text)
	{
		if (inputPaths.Count == 0 || string.IsNullOrWhiteSpace(outputPath))
		{
			return;
		}

		string fullOutput = Path.GetFullPath(outputPath);
		foreach (string input in inputPaths)
		{
			string fullInput = Path.GetFullPath(input);
			if (string.Equals(fullInput, fullOutput, StringComparison.OrdinalIgnoreCase)
				|| fullInput.StartsWith(fullOutput + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				MessageBox.Show(this, string.Format(RuriLocalization.ExportOutputInsideInput, fullOutput),
					text.Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}
		}

		if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
		{
			DialogResult result = MessageBox.Show(
				this,
				string.Format(RuriLocalization.ExportOutputNonEmpty, outputPath),
				text.Caption,
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Warning);
			if (result != DialogResult.Yes)
			{
				SetStatus(RuriLocalization.ExportCancelled);
				return;
			}
		}

		ResetLoadedSession();
		_adapter.Reset();
		ResetForm();

		HookConfig originalConfig = new()
		{
			EnabledHooks = new HashSet<string>(_hookConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase),
		};
		bool savedHeadless = GameFileLoader.Headless;
		bool hookSetChanged = extraHooks.Any(h => !_hookConfig.EnabledHooks.Contains(h));

		ToggleUi(false);
		try
		{
			if (hookSetChanged)
			{
				HookConfig augmented = new()
				{
					EnabledHooks = new HashSet<string>(_hookConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase),
				};
				foreach (string h in extraHooks)
				{
					augmented.EnabledHooks.Add(h);
				}
				SetStatus(text.Preparing);
				await ApplyHookConfigurationAsync(augmented, reloadCurrentPaths: false);
			}

			applyOverrides();
			GameFileLoader.Headless = true;

			string[] pathArray = inputPaths.ToArray();
			string loadLabel = inputPaths.Count == 1 ? inputPaths[0] : $"{inputPaths.Count}";
			SetStatus(string.Format(text.Loading, loadLabel));
			await Task.Run(() => GameFileLoader.LoadAndProcess(pathArray));

			SetStatus(string.Format(text.Exporting, outputPath));
			Logger.Info(LogCategory.Export, $"Filtered export -> {outputPath} (hooks: {string.Join(", ", extraHooks)})");
			await Task.Run(async () => await GameFileLoader.ExportUnityProject(outputPath));

			SetStatus(string.Format(text.Done, outputPath));
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), text.FailedCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
			SetStatus(text.FailedStatus);
		}
		finally
		{
			restoreOverrides();
			GameFileLoader.Headless = savedHeadless;
			if (hookSetChanged)
			{
				try
				{
					await ApplyHookConfigurationAsync(originalConfig, reloadCurrentPaths: false);
				}
				catch (Exception ex)
				{
					Logger.Warning(LogCategory.Export, $"Filtered export: failed to restore hook config: {ex.Message}");
				}
			}

			_adapter.Reset();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			ToggleUi(true);
		}
	}
}
