using AssetRipper.Import.Structure.Platforms;
using System.Reflection;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;

public class PlatformGameStructureHook_CollectAssetBundles : CommonHook, IHookModule
{
    private static readonly MethodInfo AddAssetBundle = typeof(PlatformGameStructure)
        .GetMethod("AddAssetBundle", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);

    public delegate bool AssetBundlesCheckDelegate(string filePath);

    // Static callback
    public static AssetBundlesCheckDelegate CustomAssetBundlesCheck;
    
    // Instance callback holder
    private readonly AssetBundlesCheckDelegate _moduleCallback;

    public PlatformGameStructureHook_CollectAssetBundles(AssetBundlesCheckDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomAssetBundlesCheck = _moduleCallback;
    }

    [RetargetMethod(typeof(PlatformGameStructure), "CollectAssetBundles")]
    public void CollectAssetBundles(string root, List<KeyValuePair<string, string>> files)
    {
        var _this = (PlatformGameStructure)(object)this;
        var fs = _this.FileSystem;

        foreach (string file in fs.Directory.EnumerateFiles(root))
        {
            if (CustomAssetBundlesCheck(file))
            {
                string name = fs.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                AddAssetBundle.Invoke(null, new object[] { files, name, file });
            }
        }
    }
}