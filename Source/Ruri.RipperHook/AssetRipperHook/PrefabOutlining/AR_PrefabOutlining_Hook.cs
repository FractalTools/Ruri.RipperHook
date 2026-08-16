using AssetRipper.Processing;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR_PrefabOutlining;

[RipperFeature("PrefabOutlining")]
public partial class AR_PrefabOutlining_Hook : RipperHookCommon
{
    protected AR_PrefabOutlining_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.Register(new AssetProcessorRegistration
        {
            InsertBefore = typeof(LightingDataProcessor),
            Factory = PrefabOutliningProcessor,
        });
        base.InitAttributeHook();
    }
}
