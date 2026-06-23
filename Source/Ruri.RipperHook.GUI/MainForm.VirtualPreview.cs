using AssetRipper.Assets;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using AssetRipper.SourceGenerated.Classes.ClassID_83;
using AssetRipper.SourceGenerated.Classes.ClassID_213;
using Ruri.RipperHook.GUI.Services;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.RipperHook.GUI;

// On-demand preview of a CAB-map virtual file: selecting one kicks off a bundle-granular load of just that
// CAB (+ its dependency closure) in the background, and as soon as it's read the real asset is rendered —
// the texture shows, the mesh shows, and the info panel gets the real size and type details. The list stays
// in CAB-map mode; results are cached per CAB so re-selecting is instant. All loads share one lock so a
// preview never races an explicit Load/Export of the same global AssetRipper state.
public partial class MainForm
{
	private readonly Dictionary<string, PreviewData> _virtualPreviewCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _adapterLoadLock = new(1, 1);

	private async void PreviewVirtualFileAsync(ExportCabMap.CabRow row)
	{
		int requestVersion = _previewRequestVersion;   // bumped by the selection handler that called us
		string cab = row.Cab;

		if (_virtualPreviewCache.TryGetValue(cab, out PreviewData? cached))
		{
			RenderPreview(cached);
			return;
		}
		if (!_exportMap.HasMap)
		{
			return;
		}

		try
		{
			await Task.Delay(220);   // debounce arrow-key navigation — only load once the selection settles
			if (requestVersion != _previewRequestVersion)
			{
				return;
			}

			(string[] files, HashSet<string> fileNames) = _exportMap.ResolveScopedClosure([cab]);
			if (files.Length == 0)
			{
				return;
			}

			string baseInfo = assetInfoLabel.Text;
			assetInfoLabel.Text = baseInfo + "\r\n\r\nLoading preview…";

			PreviewData? preview = null;
			await _adapterLoadLock.WaitAsync();
			try
			{
				if (requestVersion != _previewRequestVersion)
				{
					return;
				}
				// Append the previewed closure into the shared (global) AssetRipper state — there is only one —
				// so the real asset can be read and it joins the loaded Asset List. Append (never reset) matches
				// the load model, so previewing a virtual file never wipes previously loaded assets. The loaded
				// Asset List is refreshed lazily (when that tab is next shown) to avoid clearing assetListView's
				// selection / bumping the preview version mid-render.
				foreach (string fileName in fileNames)
				{
					_scopedLoadFilter.Add(fileName);
				}
				string[] nextPaths = _lastLoadedPaths.Concat(files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
				GameBundleHook.LoadIncludeFile = _scopedLoadFilter.Count > 0 ? name => _scopedLoadFilter.Contains(name) : null;
				try
				{
					await Task.Run(() => _adapter.LoadPaths(nextPaths));
				}
				finally
				{
					GameBundleHook.LoadIncludeFile = null;
				}
				if (requestVersion != _previewRequestVersion)
				{
					return;
				}
				RememberLoadSession(nextPaths, LoadSessionKind.MixedPaths);
				_assetListDirty = true;
				RipperAssetEntry? main = PickPreviewAsset(cab);
				if (main is not null)
				{
					preview = await Task.Run(() => _adapter.GetPreviewWithSize(main));
				}
			}
			finally
			{
				_adapterLoadLock.Release();
			}

			if (requestVersion != _previewRequestVersion)
			{
				return;
			}
			if (preview is not null)
			{
				if (_virtualPreviewCache.Count > 64)
				{
					_virtualPreviewCache.Clear();   // simple cap — keep the cache from growing unbounded
				}
				_virtualPreviewCache[cab] = preview;
				RenderPreview(preview);
			}
			else
			{
				assetInfoLabel.Text = baseInfo + "\r\n\r\n(no directly previewable asset in this CAB)";
			}
		}
		catch (Exception exception)
		{
			if (requestVersion == _previewRequestVersion)
			{
				assetInfoLabel.Text = $"Preview failed: {exception.GetType().Name}: {exception.Message}";
			}
		}
	}

	/// <summary>The most previewable asset hosted by <paramref name="cab"/> among the just-loaded closure.</summary>
	private RipperAssetEntry? PickPreviewAsset(string cab)
	{
		RipperAssetEntry? best = null;
		int bestRank = int.MaxValue;
		foreach (RipperAssetEntry entry in _adapter.Assets)
		{
			if (!string.Equals(entry.SourceFile, cab, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			int rank = PreviewRank(entry.Asset);
			if (rank < bestRank)
			{
				bestRank = rank;
				best = entry;
				if (rank == 0) break;
			}
		}
		return best;
	}

	private static int PreviewRank(IUnityObjectBase asset) => asset switch
	{
		ITexture2D => 0,
		ISprite => 0,
		IMesh => 1,
		IAudioClip => 1,
		ITextAsset => 2,
		IShader => 2,
		IGameObject => 3,
		IMaterial => 4,
		_ => 5,
	};
}
