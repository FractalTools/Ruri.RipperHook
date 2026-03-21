using AssetRipper.Primitives;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;

namespace Ruri.RipperHook.Endfield;

[GameHook("Endfield", "0.5.27", "2021.3.34f1")]
public partial class EndField_0_5_27_Hook : EndFieldCommon_Hook
{
    public static UnityVersion endFieldClassVersion = new UnityVersion(2021, 3, 527, UnityVersionType.Experimental, (byte)CustomEngineType.EndField);

    private static EndField_0_5_27_FairGuardDecryptor fairGuardDecryptor;

    protected EndField_0_5_27_Hook()
    {
        fairGuardDecryptor = new EndField_0_5_27_FairGuardDecryptor();
    }

    protected override UnityVersion GetTargetVersion(GameHookAttribute attr)
    {
        return endFieldClassVersion;
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new GameBundleHook(CustomFilePreInitialize));
        RegisterModule(new PlatformGameStructureHook_CollectAssetBundles(CustomAssetBundlesCheck));
        RegisterModule(new PlatformGameStructureHook_IsBundleHeader(CustomAssetBundlesCheckMagicNum));

        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));

        base.InitAttributeHook();
    }
}