using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.GUI;

// GUI counterpart to AR_ExportDirectly_Hook: pick an input (single file for spot
// testing, or a folder for whole-game), load, then export straight to a sibling
// "<name>Output" folder. Scene tree and asset list are NOT populated — whole-game
// exports happen here and the in-memory views would blow up.
public partial class MainForm
{
	private async void directExportFromFileToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new()
		{
			Multiselect = true,
			CheckFileExists = true,
			Title = "Select asset file(s) for direct export",
			Filter = "All files|*.*|AssetBundle|*.ab;*.bundle;*.unity3d;*.assets",
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		await RunDirectExportAsync(dialog.FileNames);
	}

	private async void directExportFromFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		using FolderBrowserDialog dialog = new()
		{
			Description = "Select a game root to load and export directly",
			UseDescriptionForTitle = true,
		};
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		await RunDirectExportAsync(new[] { dialog.SelectedPath });
	}

	private async Task RunDirectExportAsync(IReadOnlyList<string> inputPaths)
	{
		if (inputPaths.Count == 0)
		{
			return;
		}

		// Output dir is derived from the first input — mirrors AR_ExportDirectly_Hook.
		// For multi-file selection that's "near enough"; users selecting many files share a
		// parent folder anyway.
		string outputPath = ComputeDirectExportOutputPath(inputPaths[0]);

		// Web UI's ExportUnityProject would pop a native confirm dialog when the target is
		// non-empty; settle this in WinForms instead so the prompt sits over MainForm.
		if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
		{
			DialogResult result = MessageBox.Show(
				this,
				$"Output folder already exists and is non-empty:{Environment.NewLine}{outputPath}{Environment.NewLine}{Environment.NewLine}Delete its contents and continue?",
				"Direct Export",
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Warning);
			if (result != DialogResult.Yes)
			{
				SetStatus("Direct export aborted.");
				return;
			}
		}

		// Whatever the user had loaded before is dropped — direct export is a one-shot,
		// not a side activity layered on top of the current session.
		ResetLoadedSession();
		_adapter.Reset();
		ResetForm();

		string loadLabel = inputPaths.Count == 1 ? inputPaths[0] : $"{inputPaths.Count} paths";
		SetStatus($"Direct export: loading {loadLabel}...");
		ToggleUi(false);
		bool savedHeadless = GameFileLoader.Headless;
		try
		{
			// Suppress the AssetRipper-native confirmation dialog inside ExportUnityProject;
			// we already confirmed above.
			GameFileLoader.Headless = true;

			string[] pathArray = inputPaths.ToArray();
			await Task.Run(() =>
			{
				GameFileLoader.LoadAndProcess(pathArray);
			});

			SetStatus($"Direct export: exporting to {outputPath}...");
			Logger.Info(LogCategory.Export, $"Direct export -> {outputPath}");

			await Task.Run(async () =>
			{
				await GameFileLoader.ExportUnityProject(outputPath);
			});

			SetStatus($"Direct export finished: {outputPath}");
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Direct export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
			SetStatus("Direct export failed.");
		}
		finally
		{
			GameFileLoader.Headless = savedHeadless;
			// Whole-game GameData stays huge; drop it now so the GUI returns to baseline
			// memory instead of holding the entire bundle graph until the user resets.
			_adapter.Reset();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			ToggleUi(true);
		}
	}

	// Mirrors AR_ExportDirectly_Hook.GameFileLoaderHook: derive the sibling folder name
	// from the input. Folder input uses its own name; file input uses the file stem.
	private static string ComputeDirectExportOutputPath(string inputPath)
	{
		string trimmed = inputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string? parent = Path.GetDirectoryName(trimmed);
		string stem = Directory.Exists(trimmed)
			? Path.GetFileName(trimmed)
			: Path.GetFileNameWithoutExtension(trimmed);

		if (string.IsNullOrEmpty(stem))
		{
			stem = "Export";
		}

		return Path.Combine(parent ?? string.Empty, $"{stem}Output");
	}
}
