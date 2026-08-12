using System.Collections.Generic;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR;

/// <summary>
/// OPT-IN export-time humanoid resolution, for Unity-bound project exports: with this hook
/// ticked, every muscle-encoded AnimationClip in the export is resolved into ordinary per-bone
/// transform curves (Humanoid.HumanoidClipGenericizer, the one solver) before serialization, so
/// the exported .anim plays on any rig with no humanoid setup. Left unticked, clips keep their
/// muscle encoding -- the portable form; the Blender importer needs neither (it solves at
/// clip-bind time against the armature's own stamped avatar).
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
