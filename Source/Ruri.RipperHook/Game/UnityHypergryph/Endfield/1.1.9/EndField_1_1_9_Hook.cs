using AssetRipper.Primitives;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleFileHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleHeaderHook;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;

namespace Ruri.RipperHook.Endfield;

[GameHook("EndField", "1.1.9", "2021.3.34f1")]
public partial class EndField_1_1_9_Hook : EndFieldCommon_Hook
{
    public static UnityVersion endFieldClassVersion = new UnityVersion(2021, 3, 119, UnityVersionType.Experimental, (byte)CustomEngineType.EndField);
    private static VFSDecryptor vfsDecryptor;

    protected EndField_1_1_9_Hook()
    {
        vfsDecryptor = new VFSDecryptor();
    }

    protected override UnityVersion GetTargetVersion(GameHookAttribute attr)
    {
        return endFieldClassVersion;
    }

    protected override void InitAttributeHook()
    {
        base.InitAttributeHook();
    }
}