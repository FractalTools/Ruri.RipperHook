
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.UnityChina;

namespace Ruri.RipperHook.AzurPromilia;

[GameHook("AzurPromilia", "0.1.1.3")]
public partial class AzurPromilia_0_1_1_3_Hook : UnityChinaCommon_Hook
{
    protected AzurPromilia_0_1_1_3_Hook()
    {
        SetKey("AzurPromilia_0_1_1_3", "7a346c32336268352333356826333231");
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));
        base.InitAttributeHook();
    }
}