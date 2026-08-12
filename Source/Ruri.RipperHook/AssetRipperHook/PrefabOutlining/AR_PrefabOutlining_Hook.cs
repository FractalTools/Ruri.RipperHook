using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR_PrefabOutlining;

[RipperHook(GameType.AR_PrefabOutlining)]
public partial class AR_PrefabOutlining_Hook : RipperHookCommon
{
    protected AR_PrefabOutlining_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.CustomAssetProcessors.Add(PrefabOutliningProcessor);
        base.InitAttributeHook();
    }
}
