using CUE4Parse.UE4.Assets.Exports;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// One concrete component placement resolved by ComponentResolver: a single
// scene-graph leaf the exporter pipeline will hand to whichever
// IComponentExporter claims it. Every renderable family (static mesh / spline
// mesh / light / camera / landscape / ...) flows through this same struct so
// dispatch can stay "one neutral struct fans out to N exporters" instead of
// each exporter independently re-walking the actor tree.
//
// Three fields, immutable on purpose so the struct is cheap to pass by `in`:
//
//   Component       — the leaf UObject (UStaticMeshComponent / USpotLightComponent
//                     / UCameraComponent / ALandscapeProxy / ...). Concrete type
//                     decides which exporter wins; CanExport is pure type-test.
//   WorldTransform  — the component's full world-space placement, already folded
//                     through SceneTransform.CalculateTransform's AttachParent
//                     chain. Carries the `Relation` plus local TRS so per-
//                     instance overrides (UInstancedStaticMeshComponent) can
//                     still rebuild a sibling Transform via
//                     SceneTransform.InstanceTransform without re-walking.
//   OwnerActor      — the placing actor (AStaticMeshActor / AInstancedFoliageActor
//                     / BP_Boulder_C / ...). Needed by the static-mesh path
//                     because the verified Renderer.ProcessMesh reads
//                     `actor.bMirrored` and per-actor `TextureData[]` off the
//                     owner, not the component (Renderer.cs:604/606).
//
// IPropertyHolder is the common interface AActor and UObject both implement —
// it is what CUE4Parse hands the FModel preview, so keeping it on the contract
// means the lossless layer (which walks AActor[] directly) can build identical
// PlacedComponents without coercing through UObject.
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
