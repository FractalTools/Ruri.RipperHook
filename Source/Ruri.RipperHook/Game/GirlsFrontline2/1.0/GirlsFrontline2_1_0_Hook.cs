using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectStreamingAssets;

namespace Ruri.RipperHook.GirlsFrontline2;

[GameHook("GirlsFrontline2", "1.0")]
public partial class GirlsFrontline2_1_0_Hook : GirlsFrontline2Common_Hook
{
    public static readonly byte[] XorKey = { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00, 0x00, 0x00, 0x00, 0x07, 0x35, 0x2E, 0x78, 0x2E };

    protected GirlsFrontline2_1_0_Hook()
    {
        Decryptor = new XorDecryptor(XorKey);
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new GameBundleHook(CustomFilePreInitialize));
        RegisterModule(new PlatformGameStructureHook_CollectAssetBundles(CustomAssetBundlesCheck));
        RegisterModule(new PlatformGameStructureHook_CollectStreamingAssets(CustomCollectStreamingAssets));
        base.InitAttributeHook();
    }
}