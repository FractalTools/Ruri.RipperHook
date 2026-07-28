using System.Collections.Generic;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Resolves humanoid (muscle) AnimationClips into ordinary generic clips during asset processing,
/// so every exporter and every importer downstream sees one kind of clip. See
/// <see cref="HumanoidToGenericProcessor"/> for why this belongs at the asset level rather than
/// inside any one consumer.
/// </summary>
[RipperHook(GameType.AR_HumanoidToGeneric)]
public partial class AR_HumanoidToGeneric_Hook : RipperHookCommon
{
    protected AR_HumanoidToGeneric_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.CustomAssetProcessors.Add(HumanoidProcessor);
        base.InitAttributeHook();
    }

    private static IEnumerable<IAssetProcessor> HumanoidProcessor(FullConfiguration settings)
    {
        yield return new HumanoidToGenericProcessor();
    }
}
