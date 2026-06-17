using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Coordinate-space bridge between FModel's verified world preview and a glTF
// scene graph.
//
// The mesh geometry we place is produced by CUE4Parse's
// `Gltf.ExportStaticMeshSections`, which emits each vertex as
//
//     g = SwapYZ(v) * 0.01    (Unreal-local cm vertex v -> glTF-local, Y-up, metres)
//
// See Gltf.cs:287 (`SwapYZ(vert.Position*0.01f)`) and SwapYZ at Gltf.cs:301-305
// (`(X, Z, Y)`).
//
// FModel's verified Snooper preview builds the SAME vertex: StaticModel.cs:33-35
// stores `(v.X, v.Z, v.Y) * SCALE_DOWN_RATIO` = `SwapYZ(v) * 0.01` — byte-for-byte
// `g`. It then renders `g * W`, where `W` = FModel's `Transform.Matrix`
// (Transform.cs:20-23). Critically, the Y/Z swizzle, the W-negation (LH->RH) and
// the metre scaling live in the VERTEX LOADER (StaticModel.cs) and in
// `Position = location * SCALE_DOWN_RATIO`; `W` itself operates entirely in the
// glTF-local (Y-up, metres) space that `g` already lives in. So `W` is NOT a
// cm->m / Z-up->Y-up converter — it is a placement transform on top of `g`.
//
// Therefore the correct glTF node matrix for a mesh whose vertices are `g` is
// simply
//
//     N = W = placement.Matrix
//
// because FModel's reference render is exactly `g * W`. No inverse-mesh
// correction is applied: our mesh already lives in the space `W` expects, so
// any extra factor double-counts the swizzle/scale. (The prior revision
// pre-multiplied by `S^-1 = 100 * SwapYZ`, which (a) re-expanded the already-
// metric mesh back to centimetres — a 100x blow-up that swung every instance's
// huge mesh around the origin into a giant radial disk — and (b) injected a
// second Y/Z swap, leaving every node mirrored (det < 0). N = W fixes both:
// correct size and det(W) > 0, no mirror.)
//
// Lights and cameras already place via `placement.Matrix` directly (with their
// own punctual/camera axis remaps); this is the matching convention for meshes.
internal static class SceneTransform
{
    // FModel Views/Snooper/Constants.cs — 1 Unreal unit (cm) -> 1 viewer unit.
    private const float ScaleDownRatio = 0.01f;

    // The world matrix to hand SharpGLTF's SceneBuilder.AddRigidMesh for a mesh
    // built by Gltf.ExportStaticMeshSections. The mesh vertices are already in
    // FModel's glTF-local space, so the node matrix is FModel's verified
    // placement matrix verbatim.
    public static Matrix4x4 NodeMatrix(Transform placement)
    {
        return placement.Matrix;
    }

    // 1:1 port of FModel Renderer.CalculateTransform (Renderer.cs:676-690).
    // Walks the AttachParent chain so a component's placement folds in every
    // parent SceneComponent's local transform.
    public static Transform CalculateTransform(IPropertyHolder component, Transform relation)
    {
        if (component.TryGetValue(out FPackageIndex attachParent, "AttachParent") &&
            attachParent.TryLoad(out UObject parent))
        {
            relation = CalculateTransform(parent, relation);
        }

        return new Transform
        {
            Relation = relation.Matrix,
            Position = component.GetOrDefault("RelativeLocation", FVector.ZeroVector) * ScaleDownRatio,
            Rotation = component.GetOrDefault("RelativeRotation", FRotator.ZeroRotator).Quaternion(),
            Scale = component.GetOrDefault("RelativeScale3D", FVector.OneVector),
        };
    }

    // Per-instance placement for an InstancedStaticMeshComponent, matching
    // FModel Renderer.cs:549-555: the component chain becomes the Relation,
    // the per-instance transform data becomes the local part.
    public static Transform InstanceTransform(Transform componentRelation, FVector translation, FQuat rotation, FVector scale)
    {
        return new Transform
        {
            Relation = componentRelation.Matrix,
            Position = translation * ScaleDownRatio,
            Rotation = rotation,
            Scale = scale,
        };
    }
}
