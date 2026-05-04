using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Platforms;
using System.Reflection;
using System.Collections.Generic;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectStreamingAssets;

public class PlatformGameStructureHook_CollectStreamingAssets : CommonHook, IHookModule
{
    private static readonly MethodInfo CollectAssetBundlesRecursively = typeof(PlatformGameStructure)
        .GetMethod("CollectAssetBundlesRecursively", BindingFlags.Instance | BindingFlags.NonPublic);

    public delegate bool CollectStreamingAssetsDelegate(PlatformGameStructure _this, List<KeyValuePair<string, string>> files, MethodInfo CollectAssetBundlesRecursively);

    public static CollectStreamingAssetsDelegate CustomCollectStreamingAssets;
    
    private readonly CollectStreamingAssetsDelegate _moduleCallback;

    public PlatformGameStructureHook_CollectStreamingAssets(CollectStreamingAssetsDelegate callback)
    {
        _moduleCallback = callback;
    }
    
    public void OnApply()
    {
        CustomCollectStreamingAssets = _moduleCallback;
    }

    [RetargetMethod(typeof(PlatformGameStructure), nameof(CollectStreamingAssets))]
    protected void CollectStreamingAssets()
    {
        var _this = (PlatformGameStructure)(object)this;

        if (CustomCollectStreamingAssets != null &&
            CustomCollectStreamingAssets(_this, _this.Files, CollectAssetBundlesRecursively))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_this.StreamingAssetsPath))
        {
            return;
        }

        Logger.Info(LogCategory.Import, "Collecting Streaming Assets...");

        if (_this.FileSystem.Directory.Exists(_this.StreamingAssetsPath))
        {
            CollectAssetBundlesRecursively.Invoke(_this, new object[] { _this.StreamingAssetsPath, _this.Files });
        }
    }
}