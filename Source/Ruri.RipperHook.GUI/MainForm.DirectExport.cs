using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.GUI;

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

		string outputPath = ComputeDirectExportOutputPath(inputPaths[0]);

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

		ResetLoadedSession();
		_adapter.Reset();
		ResetForm();

		string loadLabel = inputPaths.Count == 1 ? inputPaths[0] : $"{inputPaths.Count} paths";
		SetStatus($"Direct export: loading {loadLabel}...");
		ToggleUi(false);
		bool savedHeadless = GameFileLoader.Headless;
		try
		{
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
			_adapter.Reset();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			ToggleUi(true);
		}
	}

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
