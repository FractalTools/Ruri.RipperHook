using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;

namespace Ruri.RipperHook.Endfield;

[GameHook("Endfield", "0.1")]
public partial class EndField_0_1_Hook : EndFieldCommon_Hook
{
    public static FairGuardDecryptor Decryptor;

    protected EndField_0_1_Hook()
    {
        Decryptor = new FairGuardDecryptor();
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));
        base.InitAttributeHook();
    }
}