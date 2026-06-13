using AssetRipper.Checksum;
using AssetRipper.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.BlendShapeData;
using AssetRipper.SourceGenerated.Subclasses.BlendShapeVertex;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShape;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShapeChannel;
using AssetRipper.SourceGenerated.Subclasses.MinMaxAABB;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;
using SystemMatrix4x4 = System.Numerics.Matrix4x4;
using SystemQuaternion = System.Numerics.Quaternion;
using SystemVector3 = System.Numerics.Vector3;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

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

        // FillWithCompressedMeshData defaults IsReadable to false (struct init zero);
        // a skinned mesh that Unity wants to bind at runtime needs CPU-readable data
        // for SkinnedMeshRenderer.BakeMesh / collider cooking — flip it back on.
        if (mesh.Has_IsReadable())
            mesh.IsReadable = true;

        SetBoneNameHashes(mesh, converted.RefSkeleton);
        SetBonesAABB(mesh, lod, converted.RefSkeleton.Count);
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

    // Keep the 4 largest influences, sort descending and renormalize so the weights
    // sum to 1. Descending order matters because AR.CompressedMesh.SetWeights stores
    // weights as int(w * 31) and stops once accumulated == 31; putting the biggest
    // weight first minimizes quantization error on the small tail. It also matches
    // Unity's BoneWeight convention ("indices are in descending order of weight").
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

        // Insertion sort, 4 elements: descending by weight.
        for (int i = 1; i < 4; i++)
        {
            float weightCarry = weights[i];
            int boneCarry = bones[i];
            int slot = i;
            while (slot > 0 && weights[slot - 1] < weightCarry)
            {
                weights[slot] = weights[slot - 1];
                bones[slot] = bones[slot - 1];
                slot--;
            }
            weights[slot] = weightCarry;
            bones[slot] = boneCarry;
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

    // Per-bone local-space AABB, one entry per ref-skeleton bone (entries align to
    // BoneNameHashes / BindPose). Unity uses these for SkinnedMeshRenderer culling
    // and updateWhenOffscreen; missing them produces over-conservative bounds and
    // off-screen culling artifacts. We compute mesh-local AABBs over every vertex
    // whose primary (largest-weight) bone is that bone, which matches Unity's
    // internal "GetBoneAABBs" semantics. Bones with no vertices get an empty box
    // (Min == Max == 0) — Unity treats that as "this bone has no skinned vertices".
    private static void SetBonesAABB(IMesh mesh, CSkelMeshLod lod, int boneCount)
    {
        if (!mesh.Has_BonesAABB() || boneCount == 0)
            return;

        CSkelMeshVertex[]? verts = lod.Verts;
        if (verts is null || verts.Length == 0)
            return;

        SystemVector3[] mins = new SystemVector3[boneCount];
        SystemVector3[] maxs = new SystemVector3[boneCount];
        bool[] seen = new bool[boneCount];

        for (int v = 0; v < verts.Length; v++)
        {
            CSkelMeshVertex vertex = verts[v];
            IReadOnlyList<BoneInfluence> influences = vertex.Influences;
            if (influences.Count == 0)
                continue;

            // Use only the strongest influence — matches Unity's per-bone bounds
            // (every vertex belongs to one "primary" bone box) without overcounting.
            int bestBone = -1;
            float bestWeight = 0f;
            for (int k = 0; k < influences.Count; k++)
            {
                BoneInfluence influence = influences[k];
                if (influence.Weight > bestWeight)
                {
                    bestWeight = influence.Weight;
                    bestBone = influence.Bone;
                }
            }
            if (bestBone < 0 || bestBone >= boneCount)
                continue;

            SystemVector3 position = new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            if (!seen[bestBone])
            {
                mins[bestBone] = position;
                maxs[bestBone] = position;
                seen[bestBone] = true;
            }
            else
            {
                mins[bestBone] = SystemVector3.Min(mins[bestBone], position);
                maxs[bestBone] = SystemVector3.Max(maxs[bestBone], position);
            }
        }

        for (int i = 0; i < boneCount; i++)
        {
            MinMaxAABB box = mesh.BonesAABB.AddNew();
            if (seen[i])
            {
                box.Min.SetValues(mins[i].X, mins[i].Y, mins[i].Z);
                box.Max.SetValues(maxs[i].X, maxs[i].Y, maxs[i].Z);
            }
            // Else leave as default zero box (bone has no skinned verts).
        }
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
