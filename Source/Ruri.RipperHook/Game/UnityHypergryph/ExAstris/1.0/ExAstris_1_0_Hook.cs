using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;

namespace Ruri.RipperHook.ExAstris;

[GameHook("ExAstris", "1.0")]
public partial class ExAstris_1_0_Hook : ExAstrisCommon_Hook
{
    protected ExAstris_1_0_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));
        base.InitAttributeHook();
    }
}