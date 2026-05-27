using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Extensions;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using Ruri.RipperHook.GUI.Components;
using Ruri.RipperHook.GUI.Services;
using Ruri.Hook.Attributes;
using Ruri.Hook.Config;
using Ruri.RipperHook.Attributes;
using System.Media;
using System.Runtime.InteropServices;

namespace Ruri.RipperHook.GUI;

public partial class MainForm : Form
{
	private enum ExportScope
	{
		All,
		Selected,
		Filtered,
	}

	private enum ExportFormat
	{
		Converted,
		Yaml,
	}

	private readonly string _configPath;
	private HookConfig _hookConfig;
	private List<(Type Type, GameHookAttribute Attribute)> _availableHooks = [];
	private readonly RuriAssetRipperAdapter _adapter = new();
	private IReadOnlyList<RipperAssetEntry> _filteredAssets = [];
	private bool _suppressHookTreeEvents;
	private Font? _hookTreeBoldFont;
	private int _sortColumn = -1;
	private bool _sortAscending = true;
	private List<TreeNode> _sceneRoots = [];
	private string[] _lastLoadedPaths = [];
	private LoadSessionKind _lastLoadSessionKind;
	private SoundPlayer? _soundPlayer;
	private MemoryStream? _audioStream;
	private bool _glControlLoaded;
	private int _mouseDownX;
	private int _mouseDownY;
	private bool _leftMouseDown;
	private bool _rightMouseDown;
	private readonly bool[] _textureChannels = [true, true, true, true];
	private const int MaxImmediatePreviewCharacters = 262144;
	private const int MaxImmediateYamlCharacters = 262144;
	private int _shaderProgram;
	private int _colorShaderProgram;
	private int _texturedShaderProgram;
	private int _wireShaderProgram;
	private int _vao;
	private int _positionVbo;
	private int _normalVbo;
	private int _colorVbo;
	private int _uvVbo;
	private int _ebo;
	private int _uniformModelMatrix;
	private int _uniformViewMatrix;
	private int _uniformProjectionMatrix;
	private int _colorUniformModelMatrix;
	private int _colorUniformViewMatrix;
	private int _colorUniformProjectionMatrix;
	private int _colorUniformChannelIndex;
	private int _texturedUniformModelMatrix;
	private int _texturedUniformViewMatrix;
	private int _texturedUniformProjectionMatrix;
	private int _texturedUniformDiffuseTexture;
	private int _wireUniformModelMatrix;
	private int _wireUniformViewMatrix;
	private int _wireUniformProjectionMatrix;
	private Vector3[] _meshVertices = [];
	private Vector3[] _meshNormals = [];
	private Vector4[] _meshColors = [];
	private Vector2[] _meshUv0 = [];
	private int[] _meshIndices = [];
	private SubMeshPreview[] _meshSubMeshes = [];
	private List<(int StartIndex, int Count, int TextureId)> _texturedSubMeshes = [];
	private byte[]? _currentImageBytes;
	private bool _currentImageIsTexture2D;
	private bool _wireframeEnabled = true;
	private int _shadeMode;
	private string? _glOverlayText;
	private AssetItem? _currentPreviewItem;
	private string? _pendingYamlText;
	private bool _yamlLoadedForCurrentSelection;
	private int _previewRequestVersion;
	private bool _previewLoadedForCurrentSelection;
	private Matrix4 _modelMatrix = Matrix4.Identity;
	private Matrix4 _viewMatrix = Matrix4.Identity;
	private Matrix4 _projectionMatrix = Matrix4.Identity;
	private static readonly string[] ShadeNames = ["Normal", "Textured", "VColor R", "VColor G", "VColor B", "VColor A"];

	public MainForm(HookConfig hookConfig, string configPath)
	{
		_hookConfig = hookConfig;
		_configPath = configPath;
		InitializeComponent();
		InitializeHookMenu();
		ResetForm();
		UpdateHookStatus();
	}

	private async void loadFile_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new()
		{
			Multiselect = true,
			CheckFileExists = true,
			Title = "Load files"
		};

		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		await LoadPathsAsync(dialog.FileNames, LoadSessionKind.Files, replaceCurrent: true);
	}

	private async void loadFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		using FolderBrowserDialog dialog = new();
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		await LoadPathsAsync([dialog.SelectedPath], LoadSessionKind.Folder, replaceCurrent: true);
	}

	private async void appendFileToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		using OpenFileDialog dialog = new()
		{
			Multiselect = true,
			CheckFileExists = true,
			Title = "Append files"
		};

		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		await LoadPathsAsync(dialog.FileNames, LoadSessionKind.Files, replaceCurrent: false);
	}

	private async void appendFolderToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		using FolderBrowserDialog dialog = new();
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		await LoadPathsAsync([dialog.SelectedPath], LoadSessionKind.Folder, replaceCurrent: false);
	}

	private async Task LoadPathsAsync(IReadOnlyList<string> paths, LoadSessionKind sessionKind, bool replaceCurrent)
	{
		string[] normalizedPaths = NormalizePaths(paths, replaceCurrent);
		if (normalizedPaths.Length == 0)
		{
			SetStatus("No valid paths were selected.");
			return;
		}

		SetStatus("Loading...");
		ToggleUi(false);
		try
		{
			await Task.Run(() => _adapter.LoadPaths(normalizedPaths));
			RememberLoadSession(normalizedPaths, sessionKind);
			RebuildLoadedState();
			SetStatus($"Loaded {_adapter.Assets.Count} assets from {normalizedPaths.Length} path(s).");
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
			SetStatus("Load failed.");
		}
		finally
		{
			ToggleUi(true);
		}
	}

	private void resetToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		ResetLoadedSession();
		_adapter.Reset();
		ResetForm();
	}

	private async void settingsToolStripMenuItem_Click(object? sender, EventArgs e)
	{
		HashSet<string> before = new(_hookConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase);
		using SettingsDialog dialog = new(_hookConfig, _configPath);
		if (dialog.ShowDialog(this) != DialogResult.OK) return;

		// Settings dialog已经把 JSON 写盘. AR_* feature 开关动了的话需要重新装 hook;
		// 纯 native AR 设置 (Audio/Image/Export 那些) 已经直接改了 GameFileLoader.Settings, 不需要重装.
		bool hookSetChanged = !before.SetEquals(_hookConfig.EnabledHooks);
		if (hookSetChanged)
		{
			await ApplyHookConfigurationAsync(_hookConfig, reloadCurrentPaths: false);
			RefreshHookTreeChecks();
		}
		else
		{
			UpdateHookStatus();
			SetStatus("Settings saved.");
		}
	}

	private async void reloadHooksButton_Click(object? sender, EventArgs e)
	{
		await ReloadCurrentFilesAsync();
	}

	private void listSearch_TextChanged(object? sender, EventArgs e)
	{
		ApplyFilter();
	}

	private void treeSearch_TextChanged(object? sender, EventArgs e)
	{
		ApplyTreeFilter();
	}

	private void typeFilterComboBox_SelectedIndexChanged(object? sender, EventArgs e)
	{
		ApplyFilter();
	}

	private void assetListView_SelectedIndexChanged(object? sender, EventArgs e)
	{
		StopAudio();
		if (assetListView.SelectedItems.Count == 0)
		{
			ShowEmptyPreview();
			return;
		}

		if (assetListView.SelectedItems.Count > 1)
		{
			_currentPreviewItem = null;
			_pendingYamlText = null;
			_yamlLoadedForCurrentSelection = false;
			_previewLoadedForCurrentSelection = false;
			_previewRequestVersion++;
			ClearMeshPreview();
			imagePreviewBox.Image?.Dispose();
			imagePreviewBox.Image = null;
			imagePreviewBox.Visible = false;
			glControl.Visible = false;
			textPreviewBox.Visible = false;
			textPreviewBox.Clear();
			audioPanel.Visible = false;
			yamlTextBox.Text = "YAML is disabled while multiple assets are selected.";
			assetInfoLabel.Text = $"{assetListView.SelectedItems.Count} assets selected. Preview is disabled for multi-select.";
			return;
		}

		AssetItem item = (AssetItem)assetListView.SelectedItems[0];
		_currentPreviewItem = item;
		_yamlLoadedForCurrentSelection = false;
		_previewLoadedForCurrentSelection = false;
		_pendingYamlText = null;
		_previewRequestVersion++;
		yamlTextBox.Text = tabControl2.SelectedTab == tabPage5 ? "Loading YAML..." : "Select the YAML tab to load structured text.";
		if (item.TreeNode is not null && sceneTreeView.SelectedNode != item.TreeNode)
		{
			sceneTreeView.SelectedNode = item.TreeNode;
		}
		ShowSelectionLoadingState();
		if (tabControl2.SelectedTab == tabPage4)
		{
			LoadPreviewForCurrentSelectionAsync();
		}
		if (tabControl2.SelectedTab == tabPage5)
		{
			LoadYamlForCurrentSelectionAsync();
		}
	}

	private void assetListView_MouseUp(object? sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Right)
		{
			return;
		}

		ListViewHitTestInfo hit = assetListView.HitTest(e.Location);
		if (hit.Item is not AssetItem item)
		{
			assetListContextMenuStrip.Close();
			return;
		}

		if (!item.Selected)
		{
			assetListView.SelectedItems.Clear();
			item.Selected = true;
			item.Focused = true;
		}
	}

	private void sceneTreeView_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
	{
		sceneTreeView.SelectedNode = e.Node;
		if (e.Node is not GameObjectTreeNode goNode)
		{
			return;
		}

		for (int i = 0; i < assetListView.Items.Count; i++)
		{
			if (assetListView.Items[i] is AssetItem item && ReferenceEquals(item.TreeNode, goNode))
			{
				assetListView.SelectedItems.Clear();
				item.Selected = true;
				item.Focused = true;
				item.EnsureVisible();
				tabControl1.SelectedTab = tabPage2;
				break;
			}
		}
	}

	private void exportAllAssetsMenuItem_Click(object? sender, EventArgs e)
	{
		ExportAssets(ExportScope.All, ExportFormat.Converted);
	}

	private void exportSelectedAssetsMenuItem_Click(object? sender, EventArgs e)
	{
		ExportAssets(ExportScope.Selected, ExportFormat.Converted);
	}

	private void exportFilteredAssetsMenuItem_Click(object? sender, EventArgs e)
	{
		ExportAssets(ExportScope.Filtered, ExportFormat.Converted);
	}

	private void exportAllYamlAssetsMenuItem_Click(object? sender, EventArgs e)
	{
		ExportAssets(ExportScope.All, ExportFormat.Yaml);
	}

	private void exportSelectedYamlAssetsMenuItem_Click(object? sender, EventArgs e)
	{
		ExportAssets(ExportScope.Selected, ExportFormat.Yaml);
	}

	private void exportFilteredYamlAssetsMenuItem_Click(object? sender, EventArgs e)
	{
		ExportAssets(ExportScope.Filtered, ExportFormat.Yaml);
	}

	private void ExportAssets(ExportScope scope, ExportFormat format)
	{
		List<RipperAssetEntry> entries = GetAssetsForExport(scope);
		if (entries.Count == 0)
		{
			MessageBox.Show(this, GetEmptyExportMessage(scope), "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		using FolderBrowserDialog dialog = new();
		if (dialog.ShowDialog(this) != DialogResult.OK)
		{
			return;
		}

		try
		{
			switch (format)
			{
				case ExportFormat.Converted:
					int exportedCount = _adapter.ExportAssets(entries, dialog.SelectedPath);
					SetStatus($"Exported {exportedCount} export item(s) from {entries.Count} asset(s) to {dialog.SelectedPath}");
					break;
				case ExportFormat.Yaml:
					_adapter.ExportYaml(entries, dialog.SelectedPath);
					SetStatus($"Export YAML completed: {entries.Count} assets.");
					break;
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private List<RipperAssetEntry> GetAssetsForExport(ExportScope scope)
	{
		return scope switch
		{
			ExportScope.All => _adapter.Assets.ToList(),
			ExportScope.Selected => assetListView.SelectedItems.Cast<AssetItem>().Select(static item => item.Entry).ToList(),
			ExportScope.Filtered => _filteredAssets.ToList(),
			_ => [],
		};
	}

	private static string GetEmptyExportMessage(ExportScope scope)
	{
		return scope switch
		{
			ExportScope.All => "No assets to export.",
			ExportScope.Selected => "Select at least one asset.",
			ExportScope.Filtered => "No filtered assets to export.",
			_ => "No assets to export.",
		};
	}

	private void tabControl2_SelectedIndexChanged(object? sender, EventArgs e)
	{
		if (assetListView.SelectedItems.Count != 1)
		{
			return;
		}

		if (tabControl2.SelectedTab == tabPage4 && !_previewLoadedForCurrentSelection)
		{
			LoadPreviewForCurrentSelectionAsync();
		}
		else if (tabControl2.SelectedTab == tabPage5)
		{
			LoadYamlForCurrentSelectionAsync();
		}
	}

	private void RebuildFilters()
	{
		typeFilterComboBox.BeginUpdate();
		typeFilterComboBox.Items.Clear();
		typeFilterComboBox.Items.Add("All");
		foreach (string type in _adapter.GetTypes())
		{
			typeFilterComboBox.Items.Add(type);
		}
		typeFilterComboBox.SelectedIndex = 0;
		typeFilterComboBox.EndUpdate();
	}

	private void ApplyFilter()
	{
		string type = typeFilterComboBox.SelectedItem as string ?? "All";
		_filteredAssets = SortAssets(_adapter.Filter(listSearch.Text, type));
		Dictionary<string, AssetItem> itemsByObjectKey = [];
		assetListView.BeginUpdate();
		assetListView.Items.Clear();
		foreach (RipperAssetEntry asset in _filteredAssets)
		{
			AssetItem item = new(asset);
			assetListView.Items.Add(item);
			itemsByObjectKey[GetObjectKey(asset)] = item;
		}
		UpdateSortIndicator();
		assetListView.EndUpdate();
		_sceneRoots = _adapter.BuildSceneTree(itemsByObjectKey).ToList();
		ApplyTreeFilter();
		SetStatus($"Showing {_filteredAssets.Count} / {_adapter.Assets.Count} assets.");
		ShowEmptyPreview();
	}

	private IReadOnlyList<RipperAssetEntry> SortAssets(IReadOnlyList<RipperAssetEntry> assets)
	{
		if (_sortColumn < 0 || assets.Count <= 1)
		{
			return assets;
		}

		IComparer<RipperAssetEntry> comparer = _sortColumn switch
		{
			0 => Comparer<RipperAssetEntry>.Create(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)),
			1 => Comparer<RipperAssetEntry>.Create(static (a, b) => string.Compare(a.Container, b.Container, StringComparison.OrdinalIgnoreCase)),
			2 => Comparer<RipperAssetEntry>.Create(static (a, b) => string.Compare(a.TypeString, b.TypeString, StringComparison.OrdinalIgnoreCase)),
			3 => Comparer<RipperAssetEntry>.Create(static (a, b) => a.PathId.CompareTo(b.PathId)),
			4 => Comparer<RipperAssetEntry>.Create(static (a, b) => a.Size.CompareTo(b.Size)),
			_ => Comparer<RipperAssetEntry>.Create(static (_, _) => 0),
		};

		List<RipperAssetEntry> sorted = [.. assets];
		sorted.Sort(comparer);
		if (!_sortAscending)
		{
			sorted.Reverse();
		}
		return sorted;
	}

	private void assetListView_ColumnClick(object? sender, ColumnClickEventArgs e)
	{
		// 三态循环: 升 → 降 → 不排序 (回到 adapter 给的原始顺序). 不同列就重新从升序开始.
		if (e.Column == _sortColumn)
		{
			if (_sortAscending)
			{
				_sortAscending = false;
			}
			else
			{
				_sortColumn = -1;
				_sortAscending = true;
			}
		}
		else
		{
			_sortColumn = e.Column;
			_sortAscending = true;
		}
		ApplyFilter();
	}

	private void UpdateSortIndicator()
	{
		for (int i = 0; i < assetListView.Columns.Count; i++)
		{
			ColumnHeader col = assetListView.Columns[i];
			string baseText = col.Text.TrimEnd(' ', '▲', '▼');
			col.Text = i == _sortColumn
				? baseText + " " + (_sortAscending ? '▲' : '▼')
				: baseText;
		}
	}

	private void RebuildSceneTree()
	{
		ApplyTreeFilter();
	}

	private void ApplyTreeFilter()
	{
		string search = treeSearch.Text;
		sceneTreeView.BeginUpdate();
		sceneTreeView.Nodes.Clear();
		foreach (TreeNode node in _sceneRoots)
		{
			if (ShouldIncludeNode(node, search))
			{
				sceneTreeView.Nodes.Add(node);
				ExpandMatchingNodes(node, search);
			}
		}
		sceneTreeView.EndUpdate();
	}

	private static bool ShouldIncludeNode(TreeNode node, string search)
	{
		if (string.IsNullOrWhiteSpace(search) || node.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		foreach (TreeNode child in node.Nodes)
		{
			if (ShouldIncludeNode(child, search))
			{
				return true;
			}
		}
		return false;
	}

	private static void ExpandMatchingNodes(TreeNode node, string search)
	{
		bool hasSearch = !string.IsNullOrWhiteSpace(search);
		if (hasSearch && ShouldIncludeNode(node, search))
		{
			node.Expand();
		}
		foreach (TreeNode child in node.Nodes)
		{
			ExpandMatchingNodes(child, search);
		}
	}

	private void ResetForm()
	{
		StopAudio();
		ClearMeshPreview();
		_filteredAssets = [];
		assetListView.Items.Clear();
		sceneTreeView.Nodes.Clear();
		_sceneRoots.Clear();
		listSearch.Clear();
		treeSearch.Clear();
		typeFilterComboBox.Items.Clear();
		typeFilterComboBox.Items.Add("All");
		typeFilterComboBox.SelectedIndex = 0;
		Text = "RuriAssetRipper";
		ShowEmptyPreview();
		RefreshHookTreeChecks();
		UpdateHookStatus();
	}

	private void RebuildLoadedState()
	{
		RebuildFilters();
		ApplyFilter();
		RebuildSceneTree();
		Text = $"RuriAssetRipper - {_adapter.Assets.Count} assets";
	}

	private void RememberLoadSession(string[] paths, LoadSessionKind sessionKind)
	{
		_lastLoadedPaths = paths;
		_lastLoadSessionKind = sessionKind;
	}

	private void ResetLoadedSession()
	{
		_lastLoadedPaths = [];
		_lastLoadSessionKind = LoadSessionKind.None;
	}

	private string[] NormalizePaths(IReadOnlyList<string> paths, bool replaceCurrent)
	{
		IEnumerable<string> source = replaceCurrent
			? paths
			: _lastLoadedPaths.Concat(paths);

		return source
			.Select(static path => path.Trim())
			.Where(static path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string GetObjectKey(RipperAssetEntry entry)
	{
		return entry.SourceFile + "|" + entry.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private GameType GetSelectedGameType()
	{
		HashSet<string> enabled = _hookConfig.EnabledHooks;
		if (enabled.Count == 0)
		{
			return GameType.Unknown;
		}

		foreach ((Type _, GameHookAttribute attribute) in _availableHooks)
		{
			string hookId = $"{attribute.GameName}_{attribute.Version}";
			if (enabled.Contains(hookId) && attribute is RipperHookAttribute ripperAttribute)
			{
				return ripperAttribute.GameType;
			}
		}

		return GameType.Unknown;
	}

	private void ShowEmptyPreview()
	{
		ClearMeshPreview();
		_currentPreviewItem = null;
		_pendingYamlText = null;
		_yamlLoadedForCurrentSelection = false;
		_previewLoadedForCurrentSelection = false;
		_previewRequestVersion++;
		imagePreviewBox.Image?.Dispose();
		imagePreviewBox.Image = null;
		imagePreviewBox.Visible = false;
		glControl.Visible = false;
		textPreviewBox.Visible = false;
		textPreviewBox.Clear();
		audioPanel.Visible = false;
		yamlTextBox.Clear();
		assetInfoLabel.Text = _adapter.IsLoaded ? "Select an asset to preview." : "No asset loaded.";
	}

	private void RenderPreview(PreviewData preview)
	{
		assetInfoLabel.Text = preview.InfoText;
		ClearMeshPreview();
		_currentImageBytes = null;
		_currentImageIsTexture2D = false;
		imagePreviewBox.Image?.Dispose();
		imagePreviewBox.Image = null;
		imagePreviewBox.Visible = false;
		glControl.Visible = false;
		textPreviewBox.Visible = false;
		textPreviewBox.Clear();
		audioPanel.Visible = false;

			switch (preview.Kind)
			{
				case PreviewKind.Image:
					imagePreviewBox.Visible = true;
				_currentImageBytes = preview.Data?.ToArray();
				_currentImageIsTexture2D = assetListView.SelectedItems.Count > 0
					&& assetListView.SelectedItems[0] is AssetItem imageItem
					&& imageItem.Entry.Asset is ITexture2D;
				RefreshImagePreview();
				break;
			case PreviewKind.Mesh:
				if (preview.Payload is MeshPreviewPayload meshPayload)
				{
					RenderMeshPreview(meshPayload);
				}
				break;
			case PreviewKind.Text:
			case PreviewKind.Yaml:
				textPreviewBox.Visible = true;
				textPreviewBox.Text = CreatePreviewText(preview.TextContent, MaxImmediatePreviewCharacters, "Preview text truncated for responsiveness.");
				break;
			case PreviewKind.Audio:
				audioPanel.Visible = true;
				audioStatusLabel.Text = $"Decoded audio preview ({preview.Extension ?? "bin"}).";
				TryPlayAudio(preview.Data!, preview.Extension);
				break;
		}
	}

	private void ShowSelectionLoadingState()
	{
		ClearMeshPreview();
		imagePreviewBox.Image?.Dispose();
		imagePreviewBox.Image = null;
		imagePreviewBox.Visible = false;
		glControl.Visible = false;
		textPreviewBox.Visible = false;
		textPreviewBox.Clear();
		audioPanel.Visible = false;
		assetInfoLabel.Text = tabControl2.SelectedTab == tabPage4 ? "Loading preview..." : "Preview will load when the Preview tab becomes visible.";
	}

	private async void LoadPreviewForCurrentSelectionAsync()
	{
		AssetItem? item = _currentPreviewItem;
		if (item is null || tabControl2.SelectedTab != tabPage4)
		{
			return;
		}

		int requestVersion = _previewRequestVersion;
		try
		{
			PreviewData preview = await Task.Run(() => _adapter.GetPreview(item.Entry));
			if (requestVersion != _previewRequestVersion || !ReferenceEquals(item, _currentPreviewItem) || tabControl2.SelectedTab != tabPage4)
			{
				return;
			}

			_previewLoadedForCurrentSelection = true;
			RenderPreview(preview);
		}
		catch (Exception ex)
		{
			if (requestVersion != _previewRequestVersion || !ReferenceEquals(item, _currentPreviewItem))
			{
				return;
			}

			assetInfoLabel.Text = ex.Message;
			textPreviewBox.Visible = true;
			textPreviewBox.Text = ex.ToString();
		}
	}

	private async void LoadYamlForCurrentSelectionAsync()
	{
		if (_yamlLoadedForCurrentSelection || assetListView.SelectedItems.Count != 1)
		{
			return;
		}

		AssetItem? item = _currentPreviewItem;
		if (item is null)
		{
			yamlTextBox.Clear();
			return;
		}

		yamlTextBox.Text = "Loading YAML...";
		try
		{
			string yamlText = await Task.Run(() => _adapter.GetYaml(item.Entry));
			if (!ReferenceEquals(item, _currentPreviewItem))
			{
				return;
			}

			_pendingYamlText = yamlText;
			_yamlLoadedForCurrentSelection = true;
			yamlTextBox.Text = CreatePreviewText(yamlText, MaxImmediateYamlCharacters, "YAML truncated for responsiveness. Export YAML if you need the full content on disk.");
		}
		catch (Exception ex)
		{
			if (!ReferenceEquals(item, _currentPreviewItem))
			{
				return;
			}

			yamlTextBox.Text = ex.ToString();
		}
	}

	private static string CreatePreviewText(string? text, int maxCharacters, string truncationMessage)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}

		if (text.Length <= maxCharacters)
		{
			return text;
		}

		return string.Concat(
			text[..maxCharacters],
			Environment.NewLine,
			Environment.NewLine,
			$"... truncated, showing first {maxCharacters:N0} of {text.Length:N0} characters.",
			Environment.NewLine,
			truncationMessage);
	}

	private void RenderMeshPreview(MeshPreviewPayload payload)
	{
		_meshVertices = payload.Vertices;
		_meshNormals = payload.Normals;
		_meshColors = payload.Colors;
		_meshUv0 = payload.Uv0;
		_meshIndices = payload.Indices;
		_meshSubMeshes = payload.SubMeshes;
		_shadeMode = payload.Textures.Length > 0 ? 1 : 0;
		InitializeMeshTransforms(payload.Vertices);
		glControl.Visible = true;
		if (!_glControlLoaded || payload.Vertices.Length == 0 || payload.Indices.Length == 0)
		{
			return;
		}

		glControl.MakeCurrent();
		UploadMeshTextures(payload.Textures);
		CreateMeshBuffers();
		glControl.Invalidate();
		UpdateMeshPreviewStatus();
	}

	private void InitializeMeshTransforms(Vector3[] vertices)
	{
		_viewMatrix = Matrix4.CreateRotationY(-(float)Math.PI / 4f) * Matrix4.CreateRotationX(-(float)Math.PI / 6f);
		if (vertices.Length == 0)
		{
			_modelMatrix = Matrix4.Identity;
			return;
		}

		Vector3 min = vertices[0];
		Vector3 max = vertices[0];
		for (int i = 1; i < vertices.Length; i++)
		{
			min = Vector3.ComponentMin(min, vertices[i]);
			max = Vector3.ComponentMax(max, vertices[i]);
		}

		Vector3 size = max - min;
		Vector3 center = (min + max) * 0.5f;
		float scale = 2f / Math.Max(1e-5f, size.Length);
		_modelMatrix = Matrix4.CreateTranslation(-center) * Matrix4.CreateScale(scale);
	}

	private void TryPlayAudio(byte[] data, string? extension)
	{
		StopAudio();
		if (!string.Equals(extension, "wav", StringComparison.OrdinalIgnoreCase))
		{
			audioStatusLabel.Text += " Export to save/play this format externally.";
			return;
		}

		_audioStream = new MemoryStream(data, writable: false);
		_soundPlayer = new SoundPlayer(_audioStream);
		try
		{
			_soundPlayer.Play();
		}
		catch
		{
			audioStatusLabel.Text += " Playback failed.";
		}
	}

	private void StopAudio()
	{
		_soundPlayer?.Stop();
		_soundPlayer?.Dispose();
		_soundPlayer = null;
		_audioStream?.Dispose();
		_audioStream = null;
	}

	private void ClearMeshPreview()
	{
		if (!_glControlLoaded)
		{
			_texturedSubMeshes.Clear();
			_meshSubMeshes = [];
			return;
		}

		glControl.MakeCurrent();
		DeleteMeshTextures();
		DeleteMeshBuffers();
		_meshVertices = [];
		_meshNormals = [];
		_meshColors = [];
		_meshUv0 = [];
		_meshIndices = [];
		_meshSubMeshes = [];
	}

	private void glControl_Load(object? sender, EventArgs e)
	{
		InitializeOpenGl();
		_glControlLoaded = true;
	}

	private void glControl_Resize(object? sender, EventArgs e)
	{
		if (!_glControlLoaded || !glControl.Visible)
		{
			return;
		}

		glControl.MakeCurrent();
		UpdateProjectionMatrix(glControl.ClientSize);
		glControl.Invalidate();
	}

	private void glControl_Paint(object? sender, PaintEventArgs e)
	{
		if (!_glControlLoaded)
		{
			return;
		}

		glControl.MakeCurrent();
		GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
		if (_vao == 0 || _meshIndices.Length == 0)
		{
			glControl.SwapBuffers();
			return;
		}

		GL.Enable(EnableCap.DepthTest);
		GL.BindVertexArray(_vao);
		GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
		if (_shadeMode == 1 && _texturedSubMeshes.Count > 0 && _meshUv0.Length == _meshVertices.Length)
		{
			GL.UseProgram(_texturedShaderProgram);
			GL.UniformMatrix4(_texturedUniformModelMatrix, false, ref _modelMatrix);
			GL.UniformMatrix4(_texturedUniformViewMatrix, false, ref _viewMatrix);
			GL.UniformMatrix4(_texturedUniformProjectionMatrix, false, ref _projectionMatrix);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.Uniform1(_texturedUniformDiffuseTexture, 0);
			foreach ((int startIndex, int count, int textureId) in _texturedSubMeshes)
			{
				if (textureId == 0)
				{
					continue;
				}

				GL.BindTexture(TextureTarget.Texture2D, textureId);
				GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, startIndex * sizeof(int));
			}
			GL.BindTexture(TextureTarget.Texture2D, 0);

			GL.UseProgram(_shaderProgram);
			GL.UniformMatrix4(_uniformModelMatrix, false, ref _modelMatrix);
			GL.UniformMatrix4(_uniformViewMatrix, false, ref _viewMatrix);
			GL.UniformMatrix4(_uniformProjectionMatrix, false, ref _projectionMatrix);
			DrawUntexturedSubMeshes();
		}
		else
		{
			DrawSolidMesh();
		}

		if (_wireframeEnabled)
		{
			GL.UseProgram(_wireShaderProgram);
			GL.UniformMatrix4(_wireUniformModelMatrix, false, ref _modelMatrix);
			GL.UniformMatrix4(_wireUniformViewMatrix, false, ref _viewMatrix);
			GL.UniformMatrix4(_wireUniformProjectionMatrix, false, ref _projectionMatrix);
			GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
			GL.DrawElements(PrimitiveType.Triangles, _meshIndices.Length, DrawElementsType.UnsignedInt, 0);
		}
		GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
		GL.BindVertexArray(0);
		GL.Flush();
		glControl.SwapBuffers();
		DrawOverlay(e.Graphics);
	}

	protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
	{
		if ((keyData & Keys.Control) == Keys.Control)
		{
			Keys key = keyData & Keys.KeyCode;
			if (HandleTexturePreviewHotkey(key) || HandleMeshPreviewHotkey(key))
			{
				return true;
			}
		}

		return base.ProcessCmdKey(ref msg, keyData);
	}

	private void glControl_MouseWheel(object? sender, MouseEventArgs e)
	{
		if (!glControl.Visible)
		{
			return;
		}

		_viewMatrix *= Matrix4.CreateScale(1 + e.Delta / 1000f);
		glControl.Invalidate();
	}

	private void glControl_MouseDown(object? sender, MouseEventArgs e)
	{
		_mouseDownX = e.X;
		_mouseDownY = e.Y;
		_leftMouseDown = e.Button == MouseButtons.Left;
		_rightMouseDown = e.Button == MouseButtons.Right;
	}

	private void glControl_MouseMove(object? sender, MouseEventArgs e)
	{
		if (!_leftMouseDown && !_rightMouseDown)
		{
			return;
		}

		float dx = _mouseDownX - e.X;
		float dy = _mouseDownY - e.Y;
		_mouseDownX = e.X;
		_mouseDownY = e.Y;
		if (_leftMouseDown)
		{
			_viewMatrix *= Matrix4.CreateRotationX(dy * 0.01f);
			_viewMatrix *= Matrix4.CreateRotationY(dx * 0.01f);
		}
		if (_rightMouseDown)
		{
			_viewMatrix *= Matrix4.CreateTranslation(-dx * 0.003f, dy * 0.003f, 0f);
		}
		glControl.Invalidate();
	}

	private void glControl_MouseUp(object? sender, MouseEventArgs e)
	{
		if (e.Button == MouseButtons.Left)
		{
			_leftMouseDown = false;
		}
		if (e.Button == MouseButtons.Right)
		{
			_rightMouseDown = false;
		}
	}

	private void InitializeOpenGl()
	{
		UpdateProjectionMatrix(glControl.ClientSize);
		GL.ClearColor(Color.FromArgb(46, 58, 71));
		_shaderProgram = CreateShaderProgram(VertexShaderSource, FragmentShaderSource);
		_colorShaderProgram = CreateShaderProgram(VertexShaderSource, ColorChannelFragmentShaderSource);
		_texturedShaderProgram = CreateShaderProgram(TexturedVertexShaderSource, TexturedFragmentShaderSource);
		_wireShaderProgram = CreateShaderProgram(VertexShaderSource, WireframeFragmentShaderSource);
		_uniformModelMatrix = GL.GetUniformLocation(_shaderProgram, "modelMatrix");
		_uniformViewMatrix = GL.GetUniformLocation(_shaderProgram, "viewMatrix");
		_uniformProjectionMatrix = GL.GetUniformLocation(_shaderProgram, "projMatrix");
		_colorUniformModelMatrix = GL.GetUniformLocation(_colorShaderProgram, "modelMatrix");
		_colorUniformViewMatrix = GL.GetUniformLocation(_colorShaderProgram, "viewMatrix");
		_colorUniformProjectionMatrix = GL.GetUniformLocation(_colorShaderProgram, "projMatrix");
		_colorUniformChannelIndex = GL.GetUniformLocation(_colorShaderProgram, "colorChannelIndex");
		_texturedUniformModelMatrix = GL.GetUniformLocation(_texturedShaderProgram, "modelMatrix");
		_texturedUniformViewMatrix = GL.GetUniformLocation(_texturedShaderProgram, "viewMatrix");
		_texturedUniformProjectionMatrix = GL.GetUniformLocation(_texturedShaderProgram, "projMatrix");
		_texturedUniformDiffuseTexture = GL.GetUniformLocation(_texturedShaderProgram, "diffuseTexture");
		_wireUniformModelMatrix = GL.GetUniformLocation(_wireShaderProgram, "modelMatrix");
		_wireUniformViewMatrix = GL.GetUniformLocation(_wireShaderProgram, "viewMatrix");
		_wireUniformProjectionMatrix = GL.GetUniformLocation(_wireShaderProgram, "projMatrix");
	}

	private void UpdateProjectionMatrix(Size size)
	{
		GL.Viewport(0, 0, Math.Max(1, size.Width), Math.Max(1, size.Height));
		if (size.Width <= size.Height)
		{
			float k = 1f * size.Width / Math.Max(1, size.Height);
			_projectionMatrix = Matrix4.CreateScale(1, k, 0.01f);
		}
		else
		{
			float k = 1f * size.Height / Math.Max(1, size.Width);
			_projectionMatrix = Matrix4.CreateScale(k, 1, 0.01f);
		}
	}

	private void CreateMeshBuffers()
	{
		DeleteMeshBuffers();
		_vao = GL.GenVertexArray();
		GL.BindVertexArray(_vao);
		_positionVbo = CreateVbo(_meshVertices, 0, 3);
		_normalVbo = CreateVbo(_meshNormals, 1, 3);
		_colorVbo = CreateVbo(_meshColors, 2, 4);
		_uvVbo = CreateVbo(_meshUv0, 3, 2);
		_ebo = GL.GenBuffer();
		GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
		GL.BufferData(BufferTarget.ElementArrayBuffer, _meshIndices.Length * sizeof(int), _meshIndices, BufferUsageHint.StaticDraw);
		GL.BindVertexArray(0);
	}

	private int CreateVbo(Vector3[] data, int attributeIndex, int componentCount)
	{
		int vbo = GL.GenBuffer();
		GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
		GL.BufferData(BufferTarget.ArrayBuffer, data.Length * Vector3.SizeInBytes, data, BufferUsageHint.StaticDraw);
		GL.VertexAttribPointer(attributeIndex, componentCount, VertexAttribPointerType.Float, false, 0, 0);
		GL.EnableVertexAttribArray(attributeIndex);
		return vbo;
	}

	private int CreateVbo(Vector4[] data, int attributeIndex, int componentCount)
	{
		int vbo = GL.GenBuffer();
		GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
		GL.BufferData(BufferTarget.ArrayBuffer, data.Length * Vector4.SizeInBytes, data, BufferUsageHint.StaticDraw);
		GL.VertexAttribPointer(attributeIndex, componentCount, VertexAttribPointerType.Float, false, 0, 0);
		GL.EnableVertexAttribArray(attributeIndex);
		return vbo;
	}

	private int CreateVbo(Vector2[] data, int attributeIndex, int componentCount)
	{
		int vbo = GL.GenBuffer();
		GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
		GL.BufferData(BufferTarget.ArrayBuffer, data.Length * Vector2.SizeInBytes, data, BufferUsageHint.StaticDraw);
		GL.VertexAttribPointer(attributeIndex, componentCount, VertexAttribPointerType.Float, false, 0, 0);
		GL.EnableVertexAttribArray(attributeIndex);
		return vbo;
	}

	private void DeleteMeshBuffers()
	{
		if (_positionVbo != 0) GL.DeleteBuffer(_positionVbo);
		if (_normalVbo != 0) GL.DeleteBuffer(_normalVbo);
		if (_colorVbo != 0) GL.DeleteBuffer(_colorVbo);
		if (_uvVbo != 0) GL.DeleteBuffer(_uvVbo);
		if (_ebo != 0) GL.DeleteBuffer(_ebo);
		if (_vao != 0) GL.DeleteVertexArray(_vao);
		_positionVbo = 0;
		_normalVbo = 0;
		_colorVbo = 0;
		_uvVbo = 0;
		_ebo = 0;
		_vao = 0;
	}

	private void UploadMeshTextures(IReadOnlyList<MeshTexturePreview> textures)
	{
		DeleteMeshTextures();
		if (textures.Count == 0)
		{
			return;
		}

		foreach (MeshTexturePreview texture in textures)
		{
			int textureId = TryUploadTexture(texture.PngData);
			if (textureId != 0)
			{
				_texturedSubMeshes.Add((texture.StartIndex, texture.Count, textureId));
			}
		}
	}

	private void DeleteMeshTextures()
	{
		foreach ((_, _, int textureId) in _texturedSubMeshes)
		{
			if (textureId != 0)
			{
				GL.DeleteTexture(textureId);
			}
		}
		_texturedSubMeshes.Clear();
	}

	private static int TryUploadTexture(byte[] pngData)
	{
		try
		{
			using MemoryStream stream = new(pngData, writable: false);
			using Bitmap bitmap = new(stream);
			Rectangle rect = new(0, 0, bitmap.Width, bitmap.Height);
			System.Drawing.Imaging.BitmapData data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
			try
			{
				int textureId = GL.GenTexture();
				GL.BindTexture(TextureTarget.Texture2D, textureId);
				GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bitmap.Width, bitmap.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
				GL.BindTexture(TextureTarget.Texture2D, 0);
				return textureId;
			}
			finally
			{
				bitmap.UnlockBits(data);
			}
		}
		catch (Exception ex)
		{
			// PNG 损坏 / GL 上下文丢失 / OOM 都会落到这里. 调用方靠 textureId == 0 判定失败,
			// 但 GUI 用户看不到原因; 用 Warning 级让 logger sink 把详情记下来.
			Logger.Warning(LogCategory.General, $"TryUploadTexture failed: {ex.GetType().Name}: {ex.Message}");
			return 0;
		}
	}

	private void DrawUntexturedSubMeshes()
	{
		if (_texturedSubMeshes.Count == 0)
		{
			GL.DrawElements(PrimitiveType.Triangles, _meshIndices.Length, DrawElementsType.UnsignedInt, 0);
			return;
		}

		HashSet<(int StartIndex, int Count)> texturedRanges = _texturedSubMeshes
			.Select(static sm => (sm.StartIndex, sm.Count))
			.ToHashSet();

		int drawn = 0;
		foreach ((int startIndex, int count) in BuildSubMeshRanges())
		{
			drawn += count;
			if (texturedRanges.Contains((startIndex, count)))
			{
				continue;
			}

			GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, startIndex * sizeof(int));
		}

		if (drawn == 0)
		{
			GL.DrawElements(PrimitiveType.Triangles, _meshIndices.Length, DrawElementsType.UnsignedInt, 0);
		}
	}

	private IEnumerable<(int StartIndex, int Count)> BuildSubMeshRanges()
	{
		if (_meshSubMeshes.Length == 0)
		{
			yield return (0, _meshIndices.Length);
			yield break;
		}

		HashSet<(int StartIndex, int Count)> seen = [];
		foreach (SubMeshPreview subMesh in _meshSubMeshes)
		{
			if (seen.Add((subMesh.StartIndex, subMesh.Count)))
			{
				yield return (subMesh.StartIndex, subMesh.Count);
			}
		}
	}

	private bool HandleTexturePreviewHotkey(Keys key)
	{
		if (!_currentImageIsTexture2D || _currentImageBytes is null || !imagePreviewBox.Visible)
		{
			return false;
		}

		int channelIndex = key switch
		{
			Keys.R => 0,
			Keys.G => 1,
			Keys.B => 2,
			Keys.A => 3,
			_ => -1,
		};

		if (channelIndex < 0)
		{
			return false;
		}

		_textureChannels[channelIndex] = !_textureChannels[channelIndex];
		RefreshImagePreview();
		return true;
	}

	private bool HandleMeshPreviewHotkey(Keys key)
	{
		if (!glControl.Visible || _meshIndices.Length == 0)
		{
			return false;
		}

		switch (key)
		{
			case Keys.W:
				_wireframeEnabled = !_wireframeEnabled;
				UpdateMeshPreviewStatus();
				glControl.Invalidate();
				return true;
			case Keys.S:
				if (CanCycleShadeMode())
				{
					CycleShadeMode();
					UpdateMeshPreviewStatus();
					glControl.Invalidate();
					return true;
				}
				return false;
			default:
				return false;
		}
	}

	private void RefreshImagePreview()
	{
		if (_currentImageBytes is null)
		{
			return;
		}

		using MemoryStream stream = new(_currentImageBytes, writable: false);
		using Bitmap sourceBitmap = new(stream);
		Bitmap previewBitmap = new(sourceBitmap.Width, sourceBitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

		Rectangle rect = new(0, 0, sourceBitmap.Width, sourceBitmap.Height);
		System.Drawing.Imaging.BitmapData sourceData = sourceBitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		System.Drawing.Imaging.BitmapData previewData = previewBitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		try
		{
			unsafe
			{
				byte* sourceBase = (byte*)sourceData.Scan0;
				byte* previewBase = (byte*)previewData.Scan0;
				int enabledCount = _textureChannels.Count(static enabled => enabled);
				for (int y = 0; y < sourceBitmap.Height; y++)
				{
					byte* sourceRow = sourceBase + (y * sourceData.Stride);
					byte* previewRow = previewBase + (y * previewData.Stride);
					for (int x = 0; x < sourceBitmap.Width; x++)
					{
						int offset = x * 4;
						byte blue = sourceRow[offset + 0];
						byte green = sourceRow[offset + 1];
						byte red = sourceRow[offset + 2];
						byte alpha = sourceRow[offset + 3];

						previewRow[offset + 0] = _textureChannels[2] ? blue : enabledCount == 1 && _textureChannels[3] ? byte.MaxValue : byte.MinValue;
						previewRow[offset + 1] = _textureChannels[1] ? green : enabledCount == 1 && _textureChannels[3] ? byte.MaxValue : byte.MinValue;
						previewRow[offset + 2] = _textureChannels[0] ? red : enabledCount == 1 && _textureChannels[3] ? byte.MaxValue : byte.MinValue;
						previewRow[offset + 3] = _textureChannels[3] ? alpha : byte.MaxValue;
					}
				}
			}
		}
		finally
		{
			sourceBitmap.UnlockBits(sourceData);
			previewBitmap.UnlockBits(previewData);
		}

		imagePreviewBox.Image?.Dispose();
		imagePreviewBox.Image = previewBitmap;
		UpdateTexturePreviewStatus();
	}

	private void UpdateTexturePreviewStatus()
	{
		string channels = string.Concat(
			_textureChannels[0] ? "R" : string.Empty,
			_textureChannels[1] ? "G" : string.Empty,
			_textureChannels[2] ? "B" : string.Empty,
			_textureChannels[3] ? "A" : string.Empty);
		if (string.IsNullOrEmpty(channels))
		{
			channels = "None";
		}

		SetStatus($"Image preview ready. Channels: {channels}. Ctrl+R/G/B/A toggles channels.");
	}

	private void UpdateMeshPreviewStatus()
	{
		string shade = ShadeNames[Math.Clamp(_shadeMode, 0, ShadeNames.Length - 1)];
		string wire = _wireframeEnabled ? "On" : "Off";
		string texturedHint = CanCycleShadeMode()
			? " Ctrl+S cycles shading."
			: string.Empty;
		UpdateGlOverlayText();
		SetStatus($"Mesh preview ready. Shade: {shade}. Wireframe: {wire}. Ctrl+W toggles wireframe.{texturedHint} Mouse wheel zooms, left drag rotates, right drag pans.");
	}

	private void DrawSolidMesh()
	{
		if (_shadeMode is >= 2 and <= 5)
		{
			GL.UseProgram(_colorShaderProgram);
			GL.UniformMatrix4(_colorUniformModelMatrix, false, ref _modelMatrix);
			GL.UniformMatrix4(_colorUniformViewMatrix, false, ref _viewMatrix);
			GL.UniformMatrix4(_colorUniformProjectionMatrix, false, ref _projectionMatrix);
			GL.Uniform1(_colorUniformChannelIndex, _shadeMode - 2);
		}
		else
		{
			GL.UseProgram(_shaderProgram);
			GL.UniformMatrix4(_uniformModelMatrix, false, ref _modelMatrix);
			GL.UniformMatrix4(_uniformViewMatrix, false, ref _viewMatrix);
			GL.UniformMatrix4(_uniformProjectionMatrix, false, ref _projectionMatrix);
		}

		GL.DrawElements(PrimitiveType.Triangles, _meshIndices.Length, DrawElementsType.UnsignedInt, 0);
	}

	private bool CanCycleShadeMode()
	{
		return GetAvailableShadeModes().Count > 1;
	}

	private List<int> GetAvailableShadeModes()
	{
		List<int> modes = [0];
		if (_texturedSubMeshes.Count > 0 && _meshUv0.Length == _meshVertices.Length)
		{
			modes.Add(1);
		}
		modes.AddRange([2, 3, 4, 5]);
		return modes;
	}

	private void CycleShadeMode()
	{
		List<int> modes = GetAvailableShadeModes();
		int currentIndex = modes.IndexOf(_shadeMode);
		if (currentIndex < 0)
		{
			_shadeMode = modes[0];
			return;
		}

		_shadeMode = modes[(currentIndex + 1) % modes.Count];
	}

	private void UpdateGlOverlayText()
	{
		if (!glControl.Visible || _meshIndices.Length == 0)
		{
			_glOverlayText = null;
			return;
		}

		string wire = _wireframeEnabled ? " | Wire" : string.Empty;
		_glOverlayText = $"Shade: {ShadeNames[Math.Clamp(_shadeMode, 0, ShadeNames.Length - 1)]}{wire}";
	}

	private void DrawOverlay(Graphics graphics)
	{
		if (string.IsNullOrEmpty(_glOverlayText) || !glControl.Visible)
		{
			return;
		}

		using Font font = new("Consolas", 9f, FontStyle.Bold);
		Size textSize = TextRenderer.MeasureText(graphics, _glOverlayText, font, Size.Empty, TextFormatFlags.NoPadding);
		Rectangle rect = new(8, 8, textSize.Width + 12, textSize.Height + 8);
		using SolidBrush backBrush = new(Color.FromArgb(160, 0, 0, 0));
		graphics.FillRectangle(backBrush, rect);
		TextRenderer.DrawText(graphics, _glOverlayText, font, new Point(rect.X + 6, rect.Y + 4), Color.White, TextFormatFlags.NoPadding);
	}

	private static int CreateShaderProgram(string vertexSource, string fragmentSource)
	{
		int vertexShader = GL.CreateShader(ShaderType.VertexShader);
		GL.ShaderSource(vertexShader, vertexSource);
		GL.CompileShader(vertexShader);
		int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
		GL.ShaderSource(fragmentShader, fragmentSource);
		GL.CompileShader(fragmentShader);
		int program = GL.CreateProgram();
		GL.AttachShader(program, vertexShader);
		GL.AttachShader(program, fragmentShader);
		GL.BindAttribLocation(program, 0, "vertexPosition");
		GL.BindAttribLocation(program, 1, "normalDirection");
		GL.BindAttribLocation(program, 2, "vertexColor");
		GL.BindAttribLocation(program, 3, "texCoord");
		GL.LinkProgram(program);
		GL.DeleteShader(vertexShader);
		GL.DeleteShader(fragmentShader);
		return program;
	}

	private const string VertexShaderSource = "#version 330 core\nlayout(location = 0) in vec3 vertexPosition;\nlayout(location = 1) in vec3 normalDirection;\nlayout(location = 2) in vec4 vertexColor;\nuniform mat4 modelMatrix;\nuniform mat4 viewMatrix;\nuniform mat4 projMatrix;\nout vec3 fragNormal;\nout vec4 fragColor;\nvoid main()\n{\n    vec4 worldPos = modelMatrix * vec4(vertexPosition, 1.0);\n    fragNormal = normalize(mat3(modelMatrix) * normalDirection);\n    fragColor = vertexColor;\n    gl_Position = projMatrix * viewMatrix * worldPos;\n}";

	private const string TexturedVertexShaderSource = "#version 330 core\nlayout(location = 0) in vec3 vertexPosition;\nlayout(location = 3) in vec2 texCoord;\nuniform mat4 modelMatrix;\nuniform mat4 viewMatrix;\nuniform mat4 projMatrix;\nout vec2 fragTexCoord;\nvoid main()\n{\n    fragTexCoord = texCoord;\n    gl_Position = projMatrix * viewMatrix * modelMatrix * vec4(vertexPosition, 1.0);\n}";

	private const string FragmentShaderSource = "#version 330 core\nin vec3 fragNormal;\nin vec4 fragColor;\nout vec4 outputColor;\nvoid main()\n{\n    vec3 lightDir = normalize(vec3(0.45, 0.65, 0.6));\n    float diffuse = max(dot(normalize(fragNormal), lightDir), 0.2);\n    vec3 color = fragColor.rgb * diffuse;\n    outputColor = vec4(color, fragColor.a);\n}";

	private const string ColorChannelFragmentShaderSource = "#version 330 core\nin vec3 fragNormal;\nin vec4 fragColor;\nuniform int colorChannelIndex;\nout vec4 outputColor;\nvoid main()\n{\n    vec3 lightDir = normalize(vec3(0.45, 0.65, 0.6));\n    float diffuse = max(dot(normalize(fragNormal), lightDir), 0.2);\n    float channel = colorChannelIndex == 0 ? fragColor.r : colorChannelIndex == 1 ? fragColor.g : colorChannelIndex == 2 ? fragColor.b : fragColor.a;\n    outputColor = vec4(vec3(channel * diffuse), 1.0);\n}";

	private const string TexturedFragmentShaderSource = "#version 330 core\nin vec2 fragTexCoord;\nuniform sampler2D diffuseTexture;\nout vec4 outputColor;\nvoid main()\n{\n    outputColor = texture(diffuseTexture, fragTexCoord);\n}";

	private const string WireframeFragmentShaderSource = "#version 330 core\nout vec4 outputColor;\nvoid main()\n{\n    outputColor = vec4(0.05, 0.05, 0.05, 1.0);\n}";

	private void ToggleUi(bool enabled)
	{
		menuStrip1.Enabled = enabled;
		splitContainer1.Enabled = enabled;
	}

	private void SetStatus(string text)
	{
		toolStripStatusLabel1.Text = text;
	}


	private async Task ApplyHookConfigurationAsync(HookConfig newConfig, bool reloadCurrentPaths)
	{
		ToggleUi(false);
		try
		{
			string previousHooks = _hookConfig.EnabledHooks.Count == 0 ? "none" : string.Join(", ", _hookConfig.EnabledHooks.OrderBy(static x => x));
			string nextHooks = newConfig.EnabledHooks.Count == 0 ? "none" : string.Join(", ", newConfig.EnabledHooks.OrderBy(static x => x));
			string[] removedHooks = _hookConfig.EnabledHooks.Except(newConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase).OrderBy(static x => x).ToArray();
			string[] addedHooks = newConfig.EnabledHooks.Except(_hookConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase).OrderBy(static x => x).ToArray();
			_adapter.Reset();
			if (removedHooks.Length > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"[RuriHook] Unloading hook(s): {string.Join(", ", removedHooks)}");
			}
			if (addedHooks.Length > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"[RuriHook] Loading hook(s): {string.Join(", ", addedHooks)}");
			}
			_hookConfig = new HookConfig
			{
				EnabledHooks = new HashSet<string>(newConfig.EnabledHooks, StringComparer.OrdinalIgnoreCase)
			};
			_hookConfig.Save(_configPath);
			Bootstrap.ApplyHooks(_hookConfig);
			ResetForm();
			UpdateHookStatus();
			string currentHooks = _hookConfig.EnabledHooks.Count == 0 ? "none" : string.Join(", ", _hookConfig.EnabledHooks.OrderBy(static x => x));
			if (removedHooks.Length > 0)
			{
				Console.WriteLine($"[RuriHook] Unloaded hook(s): {string.Join(", ", removedHooks)}");
			}
			if (addedHooks.Length > 0)
			{
				Console.WriteLine($"[RuriHook] Loaded hook(s): {string.Join(", ", addedHooks)}");
			}

			if (reloadCurrentPaths && _lastLoadedPaths.Length > 0)
			{
				await LoadPathsAsync(_lastLoadedPaths, _lastLoadSessionKind == LoadSessionKind.None ? LoadSessionKind.Files : _lastLoadSessionKind, replaceCurrent: true);
				SetStatus($"Hooks switched: {previousHooks} -> {currentHooks}. Reloaded {_lastLoadedPaths.Length} path(s).");
			}
			else
			{
				SetStatus($"Hooks switched: {previousHooks} -> {nextHooks}. Click Reload Files to apply to current paths.");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Apply hooks failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
			UpdateHookStatus();
		}
		finally
		{
			ToggleUi(true);
		}
	}

	private void UpdateHookStatus()
	{
		string enabled = _hookConfig.EnabledHooks.Count == 0
			? "none"
			: string.Join(", ", _hookConfig.EnabledHooks.OrderBy(static x => x));
		hookSummaryLabel.Text = _hookConfig.EnabledHooks.Count == 0
			? "No hooks enabled. Toggle a version node to load a hook."
			: $"Enabled hooks: {enabled}";
		SetStatus($"Ready | Hooks: {enabled}");
	}

	private void InitializeHookMenu()
	{
		// AR_* hooks live in Settings → Features as toggleable feature flags. The tree only shows
		// game-specific hooks (Arknights, EndField, …), one version per game.
		_availableHooks = Hook.RuriHook.GetAvailableHooks();
		Dictionary<string, List<(Type Type, GameHookAttribute Attribute)>> grouped = _availableHooks
			.Where(static h => !h.Attribute.GameName.StartsWith("AR_", StringComparison.OrdinalIgnoreCase))
			.GroupBy(static h => h.Attribute.GameName)
			.OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(static g => g.Key, static g => g.OrderBy(h => h.Attribute.Version, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

		// Force HWND so HideRootCheckBox's TVM_SETITEM call has a real handle to target.
		_ = hookTreeView.Handle;

		hookTreeView.BeginUpdate();
		hookTreeView.Nodes.Clear();
		foreach ((string gameName, List<(Type Type, GameHookAttribute Attribute)> hooks) in grouped)
		{
			TreeNode gameNode = new(gameName);
			foreach ((Type _, GameHookAttribute attr) in hooks)
			{
				string hookId = $"{attr.GameName}_{attr.Version}";
				string versionText = string.IsNullOrWhiteSpace(attr.Version) ? "Default" : attr.Version;
				if (!string.IsNullOrWhiteSpace(attr.BaseEngineVersion))
				{
					versionText += $" [Based on {attr.BaseEngineVersion}]";
				}
				TreeNode versionNode = new(versionText) { Tag = hookId };
				gameNode.Nodes.Add(versionNode);
			}
			hookTreeView.Nodes.Add(gameNode);
			// 游戏节点纯粹是分组标签 —— 一个游戏只能装一个版本, 根节点摆个 checkbox 会让用户以为可以一键全勾.
			// WinForms 的 TreeView.CheckBoxes 是 all-or-nothing, 只能走 Win32 把根节点的 state image 清成 0.
			HideRootCheckBox(gameNode);
		}
		hookTreeView.EndUpdate();
		RefreshHookTreeChecks();
	}

	private const int TV_FIRST = 0x1100;
	private const int TVM_SETITEM = TV_FIRST + 63;
	private const int TVIF_STATE = 0x8;
	private const int TVIS_STATEIMAGEMASK = 0xF000;

	[StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Auto)]
	private struct TVITEM
	{
		public int mask;
		public IntPtr hItem;
		public int state;
		public int stateMask;
		[MarshalAs(UnmanagedType.LPTStr)] public string? lpszText;
		public int cchTextMax;
		public int iImage;
		public int iSelectedImage;
		public int cChildren;
		public IntPtr lParam;
	}

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref TVITEM lParam);

	private static void HideRootCheckBox(TreeNode node)
	{
		TreeView? owner = node.TreeView;
		if (owner is null || !owner.IsHandleCreated)
		{
			return;
		}
		TVITEM tvi = new()
		{
			hItem = node.Handle,
			mask = TVIF_STATE,
			stateMask = TVIS_STATEIMAGEMASK,
			state = 0,
		};
		SendMessage(owner.Handle, (uint)TVM_SETITEM, IntPtr.Zero, ref tvi);
	}

	private void UpdateGameNodeAppearance(TreeNode gameNode)
	{
		// 不能动 gameNode.Checked —— 一旦赋值, Win32 会把 state image 重新画出来, 之前藏掉的 checkbox 就回来了.
		// 改用 bold + 自动展开 来表示 "这个游戏有版本被启用了".
		bool anyActive = gameNode.Nodes.Cast<TreeNode>().Any(static n => n.Checked);
		if (anyActive)
		{
			_hookTreeBoldFont ??= new Font(hookTreeView.Font, FontStyle.Bold);
			gameNode.NodeFont = _hookTreeBoldFont;
			if (!gameNode.IsExpanded)
			{
				gameNode.Expand();
			}
		}
		else
		{
			gameNode.NodeFont = null;
		}
	}

	private void hookTreeView_BeforeCheck(object? sender, TreeViewCancelEventArgs e)
	{
		// 根节点 (游戏名) 的 checkbox 已经被 HideRootCheckBox 藏掉, 鼠标点不到 —— 但键盘空格还能切, 拦一道防止状态变化把藏掉的 state image 重新画出来.
		// 编程性赋值 e.Action == Unknown 必须放行.
		if (_suppressHookTreeEvents || e.Action == TreeViewAction.Unknown)
		{
			return;
		}
		if (e.Node?.Parent is null)
		{
			e.Cancel = true;
		}
	}

	private void hookTreeView_AfterCheck(object? sender, TreeViewEventArgs e)
	{
		TreeNode? node = e.Node;
		if (_suppressHookTreeEvents || e.Action == TreeViewAction.Unknown || node?.Parent is null)
		{
			return;
		}

		_suppressHookTreeEvents = true;
		try
		{
			if (node.Checked)
			{
				// 同游戏下版本互斥, 勾上一个就把同级的清掉.
				foreach (TreeNode sibling in node.Parent.Nodes)
				{
					if (!ReferenceEquals(sibling, node))
					{
						sibling.Checked = false;
					}
				}
			}
			UpdateGameNodeAppearance(node.Parent);
		}
		finally
		{
			_suppressHookTreeEvents = false;
		}

		HookConfig config = BuildHookConfigFromTree();
		string pendingHooks = config.EnabledHooks.Count == 0
			? "none"
			: string.Join(", ", config.EnabledHooks.OrderBy(static x => x));
		hookSummaryLabel.Text = $"Applying hooks: {pendingHooks}";
		_ = ApplyHookConfigurationAsync(config, reloadCurrentPaths: false);
	}

	private void RefreshHookTreeChecks()
	{
		_suppressHookTreeEvents = true;
		try
		{
			foreach (TreeNode gameNode in hookTreeView.Nodes)
			{
				foreach (TreeNode versionNode in gameNode.Nodes)
				{
					if (versionNode.Tag is string id)
					{
						versionNode.Checked = _hookConfig.EnabledHooks.Contains(id);
					}
				}
				UpdateGameNodeAppearance(gameNode);
			}
		}
		finally
		{
			_suppressHookTreeEvents = false;
		}
	}

	private HookConfig BuildHookConfigFromTree()
	{
		// Tree only carries game hook entries. AR_* feature toggles live in _hookConfig already
		// (managed by SettingsDialog) — preserve them when committing tree edits.
		HookConfig config = new();
		foreach (string arHook in _hookConfig.EnabledHooks.Where(h => h.StartsWith("AR_", StringComparison.OrdinalIgnoreCase)))
		{
			config.EnabledHooks.Add(arHook);
		}
		foreach (TreeNode gameNode in hookTreeView.Nodes)
		{
			foreach (TreeNode versionNode in gameNode.Nodes)
			{
				if (versionNode.Checked && versionNode.Tag is string id)
				{
					config.EnabledHooks.Add(id);
				}
			}
		}
		return config;
	}

	private async Task ReloadCurrentFilesAsync()
	{
		if (_lastLoadedPaths.Length == 0)
		{
			SetStatus("No files are currently loaded.");
			return;
		}
		// Re-apply with the current persisted hook set. No tree to read from since hooks live in the Settings dialog.
		await ApplyHookConfigurationAsync(_hookConfig, reloadCurrentPaths: true);
	}

	private enum LoadSessionKind
	{
		None,
		Files,
		Folder,
		MixedPaths,
	}
}
