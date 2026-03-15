using AssetRipper.Primitives;
using Ruri.RipperHook.HookUtils.BlocksInfoHook;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleHeaderHook;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;
using Ruri.RipperHook.UnityMihoyo;

namespace Ruri.RipperHook.StarRail;

[GameHook("StarRail", "2.0", "2019.4.34f1")]
public partial class StarRail_2_0_Hook : StarRailCommon_Hook
{
    public static UnityVersion starRailClassVersion = new UnityVersion(2019, 4, 200, UnityVersionType.Experimental, (byte)CustomEngineType.StarRail);

    protected StarRail_2_0_Hook()
    {
        MihoyoCommon.Mr0kDecryptor = new Mr0kDecryptor(Mr0kKey.Mr0kExpansionKey, initVector: Mr0kKey.Mr0kInitVector, blockKey: Mr0kKey.Mr0kBlockKey);
    }
    protected override UnityVersion GetTargetVersion(GameHookAttribute attr)
    {
        return starRailClassVersion;
    }
    protected override void InitAttributeHook()
    {
        RegisterModule(new FileStreamBundleHeaderHook(CustomReadHeader));
        RegisterModule(new BlocksInfoHook(CustomBlocksInfoRead));
        RegisterModule(new BundleFileBlockReaderHook(MihoyoCommon.CustomBlockCompression));
        RegisterModule(new PlatformGameStructureHook_IsBundleHeader(CustomAssetBundlesCheckMagicNum));
        RegisterModule(new GameBundleHook(CustomFilePreInitialize));
        base.InitAttributeHook();
    }
}