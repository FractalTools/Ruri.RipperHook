using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.UnityChina;

namespace Ruri.RipperHook.PunishingGrayRaven;

[GameHook("PunishingGrayRaven", "2.11")]
public partial class PunishingGrayRaven_2_11_Hook : UnityChinaCommon_Hook
{
    protected PunishingGrayRaven_2_11_Hook()
    {
        SetKey("PGR CN/JP/TW", "7935585076714C4F72436F6B57524961");
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));
        base.InitAttributeHook();
    }
}