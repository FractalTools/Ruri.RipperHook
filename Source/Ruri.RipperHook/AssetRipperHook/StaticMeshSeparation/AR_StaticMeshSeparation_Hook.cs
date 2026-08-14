using AssetRipper.Processing;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR;

[RipperHook(GameType.AR_StaticMeshSeparation)]
public partial class AR_StaticMeshSeparation_Hook : RipperHookCommon
{
    protected AR_StaticMeshSeparation_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.Register(new AssetProcessorRegistration
        {
            InsertBefore = typeof(LightingDataProcessor),
            Factory = StaticMeshProcessor,
        });
        base.InitAttributeHook();
    }
}
