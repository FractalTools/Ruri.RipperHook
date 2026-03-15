using AssetRipper.Primitives;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleFileHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleHeaderHook;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;

namespace Ruri.RipperHook.Endfield;

[GameHook("EndField", "0.8.25", "2021.3.34f1")]
public partial class EndField_0_8_25_Hook : EndFieldCommon_Hook
{
    public static UnityVersion endFieldClassVersion = new UnityVersion(2021, 3, 825, UnityVersionType.Experimental, (byte)CustomEngineType.EndField);
    private static VFSDecryptor vfsDecryptor;

    protected EndField_0_8_25_Hook()
    {
        vfsDecryptor = new VFSDecryptor();
    }

    protected override UnityVersion GetTargetVersion(GameHookAttribute attr)
    {
        return endFieldClassVersion;
    }

    protected override void InitAttributeHook()
    {
        AddMethodHook(typeof(EndField_0_5_27_Hook), nameof(EndField_0_5_27_Hook.Mesh_ReadRelease));

        RegisterModule(new FileStreamBundleHeaderHook(CustomReadHeader));
        RegisterModule(new FileStreamBundleFileHook(CustomReadFileStreamMetadata));
        RegisterModule(new GameBundleHook(CustomFilePreInitialize));
        RegisterModule(new PlatformGameStructureHook_CollectAssetBundles(CustomAssetBundlesCheck));
        RegisterModule(new PlatformGameStructureHook_IsBundleHeader(CustomAssetBundlesCheckMagicNum));

        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));

        base.InitAttributeHook();
    }
}