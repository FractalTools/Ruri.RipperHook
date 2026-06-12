using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.GlbSceneExport;

// Coordinate-space bridge between FModel's verified world preview and a glTF
// scene graph.
//
// FModel's Snooper builds, per placed component, a `Transform` whose `Matrix`
// maps a RAW Unreal-local vertex (Z-up, centimetres) into the viewer's glTF
// space (Y-up, metres). That matrix already does the Y/Z swizzle, the W
// negation (LH -> RH), and the 1 cm -> 1 unit scaling (SCALE_DOWN_RATIO).
// See FModel Views/Snooper/Transform.cs:20-23 and Renderer.cs:676-690.
//
// Our mesh geometry, however, is produced by CUE4Parse's `Gltf.ExportStaticMeshSections`,
// which emits each vertex ALREADY in glTF-local space (it applies SwapYZ and
// the *0.01 scale per vertex — Gltf.cs:287-290). So a node placed in the scene
// must NOT re-apply that per-vertex map. Let
//
//     S : Unreal-local -> glTF-local,   S(v) = SwapYZ(v) * 0.01   (= what the mesh export bakes in)
//     W : Unreal-local -> glTF-world    (= FModel's Transform.Matrix, the verified preview placement)
//
// then for a mesh whose vertices are already S(v), the glTF node world matrix is
//
//     N = S^-1 * W
//
// because  g_local = v . S  =>  v = g_local . S^-1  =>  g_world = v . W = g_local . (S^-1 * W).
// det(N) = det(S^-1)*det(W) > 0 (both flip handedness, the flips cancel), so N is a
// clean proper transform — no mirrored geometry. This pairs the byte-identical
// CUE4Parse mesh exporter with the byte-identical FModel placement matrix.
internal static class SceneTransform
{
    // FModel Views/Snooper/Constants.cs:21 — 1 Unreal unit (cm) -> 1 viewer unit.
    private const float ScaleDownRatio = 0.01f;

    // S^-1 in System.Numerics row-vector form: out.X = 100*v.X, out.Y = 100*v.Z,
    // out.Z = 100*v.Y. (S maps X->0.01X, Y->0.01Z, Z->0.01Y; SwapYZ is its own
    // inverse, the scale inverts 0.01 -> 100.)
    private static readonly Matrix4x4 InverseMeshLocalToGltf = new(
        100f, 0f, 0f, 0f,
        0f, 0f, 100f, 0f,
        0f, 100f, 0f, 0f,
        0f, 0f, 0f, 1f);

    // The world matrix to hand SharpGLTF's SceneBuilder.AddRigidMesh for a mesh
    // built by Gltf.ExportStaticMeshSections.
    public static Matrix4x4 NodeMatrix(Transform placement)
    {
        return Matrix4x4.Multiply(InverseMeshLocalToGltf, placement.Matrix);
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
