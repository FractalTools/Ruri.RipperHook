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

public partial class MainForm
{
	private readonly Dictionary<string, PreviewData> _virtualPreviewCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _adapterLoadLock = new(1, 1);

	private async void PreviewVirtualFileAsync(ExportCabMap.CabRow row)
	{
		int requestVersion = _previewRequestVersion;		string cab = row.Cab;

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
			await Task.Delay(220);			if (requestVersion != _previewRequestVersion)
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
					_virtualPreviewCache.Clear();				}
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
