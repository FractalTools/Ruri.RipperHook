using System.Collections.Generic;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR;

[RipperHook(GameType.AR_HumanoidToGeneric)]
public partial class AR_HumanoidToGeneric_Hook : RipperHookCommon
{
    protected AR_HumanoidToGeneric_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.Register(new AssetProcessorRegistration
        {
            InsertBefore = typeof(LightingDataProcessor),
            Factory = HumanoidProcessor,
        });
        base.InitAttributeHook();
    }

    private static IEnumerable<IAssetProcessor> HumanoidProcessor(FullConfiguration settings)
    {
        yield return new HumanoidToGenericProcessor();
    }
}
