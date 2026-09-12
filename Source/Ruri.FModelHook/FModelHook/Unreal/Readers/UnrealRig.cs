using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Dto;
using Ruri.RipperHook.Conversion;
using System.Numerics;

namespace Ruri.FModelHook.Unreal.Readers;

/// <summary>
/// A reference skeleton in Unity's terms: per bone its local transform through the basis, its
/// component-space matrix, the bind pose Unity keeps for it and the transform path a clip
/// addresses it by (bone names joined from the root down). Bones arrive parent-first, as
/// Unreal orders them, so one pass composes every matrix.
/// </summary>
public sealed class UnrealRig
{
    public readonly record struct Bone(string Name, int ParentIndex, Vector3 Position, Quaternion Rotation, Vector3 Scale, Matrix4x4 World, string Path);

    public IReadOnlyList<Bone> Bones { get; }

    public string[] Names { get; }

    public Matrix4x4[] BindPoses { get; }

    private UnrealRig(IReadOnlyList<Bone> bones)
    {
        Bones = bones;
        Names = new string[bones.Count];
        BindPoses = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            Names[i] = bones[i].Name;
            BindPoses[i] = Matrix4x4.Invert(bones[i].World, out Matrix4x4 inverse)
                ? Matrix4x4.Transpose(inverse)
                : Matrix4x4.Identity;
        }
    }

    /// <summary>The rig a skeletal mesh states: its reference skeleton, bone by bone in the mesh's own order.</summary>
    public static UnrealRig From(USkeletalMesh mesh, SourceBasis basis)
    {
        FReferenceSkeleton skeleton = mesh.ReferenceSkeleton;
        MeshBoneDto[] bones = new MeshBoneDto[skeleton.FinalRefBonePose.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            bones[i] = new MeshBoneDto(skeleton.FinalRefBoneInfo[i], skeleton.FinalRefBonePose[i]);
        }
        return From(bones, basis);
    }

    public static UnrealRig From(IReadOnlyList<MeshBoneDto> source, SourceBasis basis)
    {
        List<Bone> bones = new(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            MeshBoneDto bone = source[i];
            FTransform transform = bone.Transform;
            Vector3 position = basis.Position(transform.Translation.X, transform.Translation.Y, transform.Translation.Z);
            Quaternion rotation = basis.Rotation(transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
            Vector3 scale = basis.Scale(transform.Scale3D.X, transform.Scale3D.Y, transform.Scale3D.Z);
            Matrix4x4 local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            int parent = bone.ParentIndex;
            Matrix4x4 world = parent >= 0 && parent < i ? local * bones[parent].World : local;
            string path = parent >= 0 && parent < i ? bones[parent].Path + "/" + bone.Name : bone.Name;
            bones.Add(new Bone(bone.Name, parent, position, rotation, scale, world, path));
        }
        return new UnrealRig(bones);
    }
}
