using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.BundleFiles;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using System.Reflection;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;

public class PlatformGameStructureHook_IsBundleHeader : CommonHook, IHookModule
{
    private static readonly MethodInfo FromSerializedFile = typeof(BundleHeader).GetMethod("IsBundleHeader", ReflectionExtensions.PrivateStaticBindFlag());
    
    public delegate bool AssetBundlesMagicNumCheckDelegate(EndianReader reader, MethodInfo FromSerializedFile);

    public static AssetBundlesMagicNumCheckDelegate CustomAssetBundlesCheckMagicNum;
    private readonly AssetBundlesMagicNumCheckDelegate _moduleCallback;

    public PlatformGameStructureHook_IsBundleHeader(AssetBundlesMagicNumCheckDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomAssetBundlesCheckMagicNum = _moduleCallback;
    }

    [RetargetMethod(typeof(FileStreamBundleHeader), nameof(IsBundleHeader))]
    public static bool IsBundleHeader(EndianReader reader) => CustomAssetBundlesCheckMagicNum(reader, FromSerializedFile);
}