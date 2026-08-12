using CUE4Parse.UE4.Assets.Exports;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public readonly struct PlacedComponent
{
    public readonly UObject Component;
    public readonly Transform WorldTransform;
    public readonly IPropertyHolder OwnerActor;

    public PlacedComponent(UObject component, Transform worldTransform, IPropertyHolder ownerActor)
    {
        Component = component;
        WorldTransform = worldTransform;
        OwnerActor = ownerActor;
    }
}
