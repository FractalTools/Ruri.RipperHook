using AssetRipper.Checksum;
using AssetRipper.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.BlendShapeData;
using AssetRipper.SourceGenerated.Subclasses.BlendShapeVertex;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShape;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShapeChannel;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.FModelHook.UnityExport.Engine;
using SystemMatrix4x4 = System.Numerics.Matrix4x4;
using SystemQuaternion = System.Numerics.Quaternion;
using SystemVector3 = System.Numerics.Vector3;

namespace Ruri.FModelHook.UnityExport.Mappings;

// USkeletalMesh -> Mesh. Shares the static-mesh geometry path (VertexPacker),
// then layers on the skinning data that lives outside the vertex buffer:
//   * mesh.Skin        - top-4 bone weights per vertex
//   * mesh.BindPose    - inverse bind world matrix per bone (from the UE ref skeleton)
//   * mesh.BoneNameHashes / RootBoneNameHash - Unity CRC32 of each bone name
//   * mesh.Shapes      - blend shapes aggregated from the SEPARATE UMorphTarget objects
//
// The blend-shape step is the "cross-object aggregation" the design calls out:
// morph deltas live in independent UMorphTarget assets (USkeletalMesh.MorphTargets),
// yet land in this single Mesh.Shapes field — the source side just reaches across
// objects, the engine never needs to know.
public static class SkeletalMeshMappings
{
    public static void Register()
    {
        MapperRegistry.Map<USkeletalMesh, IMesh>(collection => collection.CreateMesh())
            .Set(t => t.Name, s => new Utf8String(s.Name))
            .After(Build);
    }

    private static void Build(USkeletalMesh source, IMesh mesh, ConversionContext context)
    {
        if (!source.TryConvert(out CSkeletalMesh converted) || converted.LODs.Count == 0)
            return;

        CSkelMeshLod lod = converted.LODs[0];
        if (lod.SkipLod || lod.Verts is null || lod.Verts.Length == 0)
            return;

        // Skeletal meshes go through AssetRipper's compressed-mesh fill, NOT the
        // uncompressed VertexPacker: modern Unity (2022.3) has no legacy m_Skin
        // field and AR's VertexData packer stubs the BlendWeight/BlendIndices
        // channels, so the only path that actually carries per-vertex weights is
        // compressedMesh.SetWeights. A correctly-skinned (lightly-quantized) mesh
        // beats a lossless-but-unskinned one. Static meshes keep the lossless path.
        BoneWeight4[] skin = BuildSkin(lod);
        SystemMatrix4x4[] bindPose = BuildBindPose(converted.RefSkeleton);
        MeshData meshData = MeshDataFactory.FromSkeletalMeshLod(lod, skin, bindPose);
        mesh.FillWithCompressedMeshData(meshData);

        SetBoneNameHashes(mesh, converted.RefSkeleton);
        SetBlendShapes(mesh, source, lod.LODIndex);
    }

    private static BoneWeight4[] BuildSkin(CSkelMeshLod lod)
    {
        CSkelMeshVertex[] verts = lod.Verts!;
        BoneWeight4[] skin = new BoneWeight4[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            skin[i] = ToBoneWeight(verts[i].Influences);
        return skin;
    }

    // Keep the 4 largest influences and renormalize so the weights sum to 1.
    private static BoneWeight4 ToBoneWeight(IReadOnlyList<BoneInfluence> influences)
    {
        Span<int> bones = stackalloc int[4];
        Span<float> weights = stackalloc float[4];
        foreach (BoneInfluence influence in influences)
        {
            int minSlot = 0;
            for (int i = 1; i < 4; i++)
                if (weights[i] < weights[minSlot]) minSlot = i;
            if (influence.Weight > weights[minSlot])
            {
                weights[minSlot] = influence.Weight;
                bones[minSlot] = influence.Bone;
            }
        }

        float sum = weights[0] + weights[1] + weights[2] + weights[3];
        if (sum > 0f)
            for (int i = 0; i < 4; i++) weights[i] /= sum;

        return new BoneWeight4(weights[0], weights[1], weights[2], weights[3], bones[0], bones[1], bones[2], bones[3]);
    }

    // Bind pose from the UE reference skeleton. UE stores each bone as a LOCAL
    // transform relative to its parent; Unity's bind pose is the inverse of the
    // bone's bind-time WORLD transform, so we accumulate the parent chain then
    // invert. Returned aligned to bone index (== mesh.BoneNameHashes order).
    private static SystemMatrix4x4[] BuildBindPose(List<CSkelMeshBone> refSkeleton)
    {
        int count = refSkeleton.Count;
        SystemMatrix4x4[] world = new SystemMatrix4x4[count];
        SystemMatrix4x4[] bindPose = new SystemMatrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            CSkelMeshBone bone = refSkeleton[i];
            SystemMatrix4x4 local =
                SystemMatrix4x4.CreateFromQuaternion(new SystemQuaternion(bone.Orientation.X, bone.Orientation.Y, bone.Orientation.Z, bone.Orientation.W))
                * SystemMatrix4x4.CreateTranslation(bone.Position.X, bone.Position.Y, bone.Position.Z);
            world[i] = bone.ParentIndex >= 0 && bone.ParentIndex < i
                ? local * world[bone.ParentIndex]
                : local;
            SystemMatrix4x4.Invert(world[i], out bindPose[i]);
        }
        return bindPose;
    }

    // Bone-name hashes (Unity = CRC32 of UTF-8 bone name), aligned to bone index.
    private static void SetBoneNameHashes(IMesh mesh, List<CSkelMeshBone> refSkeleton)
    {
        if (refSkeleton.Count == 0)
            return;

        if (mesh.Has_BoneNameHashes())
            foreach (CSkelMeshBone bone in refSkeleton)
                mesh.BoneNameHashes.Add(Crc32Algorithm.HashUTF8(bone.Name.Text));

        if (mesh.Has_RootBoneNameHash())
            mesh.RootBoneNameHash = Crc32Algorithm.HashUTF8(refSkeleton[0].Name.Text);
    }

    // Aggregate every UMorphTarget into mesh.Shapes. Each morph target becomes one
    // channel with a single frame at weight 100; its per-vertex deltas (indexed by
    // SourceIdx into the LOD vertex buffer, which CUE4Parse preserves) become the
    // flat BlendShapeVertex pool.
    private static void SetBlendShapes(IMesh mesh, USkeletalMesh source, int lodIndex)
    {
        if (!mesh.Has_Shapes())
            return;

        FPackageIndex[] morphTargets = source.MorphTargets;
        if (morphTargets is null || morphTargets.Length == 0)
            return;

        IBlendShapeData shapes = mesh.Shapes;
        foreach (FPackageIndex morphIndex in morphTargets)
        {
            if (morphIndex is null || morphIndex.IsNull)
                continue;
            if (!morphIndex.TryLoad<UMorphTarget>(out UMorphTarget? morph) || morph is null)
                continue;
            if (morph.MorphLODModels is null || lodIndex < 0 || lodIndex >= morph.MorphLODModels.Length)
                continue;

            FMorphTargetDelta[] deltas = morph.MorphLODModels[lodIndex].Vertices;
            if (deltas is null || deltas.Length == 0)
                continue;

            uint firstVertex = (uint)shapes.Vertices.Count;
            foreach (FMorphTargetDelta delta in deltas)
            {
                IBlendShapeVertex blendVertex = shapes.Vertices.AddNew();
                blendVertex.Vertex.CopyValues(new SystemVector3(delta.PositionDelta.X, delta.PositionDelta.Y, delta.PositionDelta.Z));
                blendVertex.Normal.CopyValues(new SystemVector3(delta.TangentZDelta.X, delta.TangentZDelta.Y, delta.TangentZDelta.Z));
                blendVertex.Index = delta.SourceIdx;
            }

            IMeshBlendShape frame = shapes.Shapes.AddNew();
            frame.FirstVertex = firstVertex;
            frame.VertexCount = (uint)deltas.Length;
            frame.HasNormals = true;
            frame.HasTangents = false;

            int frameIndex = shapes.Shapes.Count - 1;
            shapes.FullWeights.Add(100f);

            IMeshBlendShapeChannel channel = shapes.Channels.AddNew();
            channel.SetValues(morph.Name, frameIndex, 1);
        }
    }
}
