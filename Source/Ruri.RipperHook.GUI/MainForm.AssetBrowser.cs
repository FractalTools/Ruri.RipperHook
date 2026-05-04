using Ruri.RipperHook.GUI.Services;

namespace Ruri.RipperHook.GUI;

public partial class MainForm
{
	private AssetBrowser? _assetBrowser;

	protected override void OnShown(EventArgs e)
	{
		base.OnShown(e);
		EnsureAssetBrowserMenu();
	}

	internal async Task LoadAssetBrowserPathsAsync(IReadOnlyList<string> paths, bool append)
	{
		string[] nextPaths = append
			? _lastLoadedPaths.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
			: paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

		await LoadPathsAsync(nextPaths, LoadSessionKind.MixedPaths, replaceCurrent: true);
	}

	internal void ShowCabMapRequiredDialog()
	{
		MessageBox.Show(this,
			"CAB export requires a CABMap. Load a matching .bin file before exporting from the Asset Browser.",
			"CABMap Not Found",
			MessageBoxButtons.OK,
			MessageBoxIcon.Warning);
	}

	private void EnsureAssetBrowserMenu()
	{
		if (fileToolStripMenuItem.DropDownItems.OfType<ToolStripItem>().Any(static item => string.Equals(item.Name, "openAssetBrowserMenuItem", StringComparison.Ordinal)))
		{
			return;
		}

		ToolStripSeparator separator = new() { Name = "assetBrowserSeparator" };
		ToolStripMenuItem openAssetBrowserMenuItem = new("Open Asset Browser") { Name = "openAssetBrowserMenuItem" };
		openAssetBrowserMenuItem.Click += openAssetBrowserMenuItem_Click;
		fileToolStripMenuItem.DropDownItems.Insert(Math.Max(0, fileToolStripMenuItem.DropDownItems.Count - 1), separator);
		fileToolStripMenuItem.DropDownItems.Insert(Math.Max(0, fileToolStripMenuItem.DropDownItems.Count - 1), openAssetBrowserMenuItem);
	}

	private void openAssetBrowserMenuItem_Click(object? sender, EventArgs e)
	{
		if (_assetBrowser is null || _assetBrowser.IsDisposed)
		{
			_assetBrowser = new AssetBrowser(this);
		}

		_assetBrowser.Show(this);
		_assetBrowser.BringToFront();
	}
}
