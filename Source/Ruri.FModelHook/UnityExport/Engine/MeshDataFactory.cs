using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Meshes;
using SystemVector2 = System.Numerics.Vector2;
using SystemVector3 = System.Numerics.Vector3;
using SystemVector4 = System.Numerics.Vector4;

namespace Ruri.FModelHook.UnityExport.Engine;

// Builds AssetRipper MeshData (managed vertex arrays) from CUE4Parse-Conversion's
// decoded mesh LODs. CUE4Parse already unpacks FPackedNormal / half UVs into
// clean float arrays, so this just transcribes them and applies the UE->Unity
// UV V-flip (UE is DirectX V-down, Unity is OpenGL V-up).
//
// NOTE: positions are transcribed in raw Unreal axes (Z-up, centimetres). A
// uniform UE->Unity basis change (Y/Z swap + 0.01 scale) is a clean follow-up
// linear transform; the vertex DATA here is already lossless and complete.
public static class MeshDataFactory
{
    public static MeshData FromStaticMeshLod(CStaticMeshLod lod) => FromBaseLod(lod, lod.Verts!);

    // CSkelMeshVertex derives from CMeshVertex, so skeletal LODs share the exact
    // geometry transcription; the caller supplies skin (per-vertex bone weights)
    // and bind pose, which the compressed-mesh fill consumes alongside geometry.
    public static MeshData FromSkeletalMeshLod(CSkelMeshLod lod, BoneWeight4[] skin, System.Numerics.Matrix4x4[] bindPose)
        => FromBaseLod(lod, lod.Verts!) with { Skin = skin, BindPose = bindPose };

    private static MeshData FromBaseLod(CBaseMeshLod lod, CMeshVertex[] verts)
    {
        int count = lod.NumVerts;

        SystemVector3[] vertices = new SystemVector3[count];
        SystemVector3[]? normals = lod.HasNormals ? new SystemVector3[count] : null;
        SystemVector4[]? tangents = lod.HasTangents ? new SystemVector4[count] : null;
        SystemVector2[] uv0 = new SystemVector2[count];
        ColorFloat[]? colors = lod.VertexColors is { Length: > 0 } ? new ColorFloat[count] : null;

        for (int i = 0; i < count; i++)
        {
            CMeshVertex vertex = verts[i];
            vertices[i] = new SystemVector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            if (normals != null)
                normals[i] = new SystemVector3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
            if (tangents != null)
                tangents[i] = new SystemVector4(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z, vertex.Tangent.W != 0f ? vertex.Tangent.W : 1f);
            uv0[i] = new SystemVector2(vertex.UV.U, 1f - vertex.UV.V);
            if (colors != null)
            {
                FColor color = lod.VertexColors![i];
                colors[i] = new ColorFloat(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
            }
        }

        SystemVector2[]?[] extraUv = BuildExtraUv(lod, count);

        uint[] indices = lod.Indices?.Value ?? [];
        SubMeshData[] subMeshes = BuildSubMeshes(lod, vertices, indices);

        return new MeshData(
            vertices, normals, tangents, colors,
            uv0, extraUv[0], extraUv[1], extraUv[2], extraUv[3], extraUv[4], extraUv[5], extraUv[6],
            Skin: null, BindPose: null,
            indices, subMeshes);
    }

    // Extra UV channels (UV1..UV7). lod.ExtraUV is [NumTexCoords-1][NumVerts].
    private static SystemVector2[]?[] BuildExtraUv(CBaseMeshLod lod, int count)
    {
        SystemVector2[]?[] extraUv = new SystemVector2[]?[7];
        if (lod.ExtraUV is not { } extraLazy)
            return extraUv;

        FMeshUVFloat[][] extra = extraLazy.Value;
        for (int channel = 0; channel < extra.Length && channel < 7; channel++)
        {
            FMeshUVFloat[] source = extra[channel];
            if (source.Length != count) continue;
            SystemVector2[] destination = new SystemVector2[count];
            for (int i = 0; i < count; i++)
                destination[i] = new SystemVector2(source[i].U, 1f - source[i].V);
            extraUv[channel] = destination;
        }
        return extraUv;
    }

    private static SubMeshData[] BuildSubMeshes(CBaseMeshLod lod, SystemVector3[] vertices, uint[] indices)
    {
        CMeshSection[] sections = lod.Sections?.Value ?? [];
        if (sections.Length == 0)
            return [MakeSubMesh(vertices, indices, 0, indices.Length)];

        SubMeshData[] result = new SubMeshData[sections.Length];
        for (int s = 0; s < sections.Length; s++)
            result[s] = MakeSubMesh(vertices, indices, sections[s].FirstIndex, sections[s].NumFaces * 3);
        return result;
    }

    private static SubMeshData MakeSubMesh(SystemVector3[] vertices, uint[] indices, int firstIndex, int indexCount)
    {
        uint minVertex = uint.MaxValue;
        uint maxVertex = 0;
        int end = Math.Min(firstIndex + indexCount, indices.Length);
        for (int i = firstIndex; i < end; i++)
        {
            uint index = indices[i];
            if (index < minVertex) minVertex = index;
            if (index > maxVertex) maxVertex = index;
        }
        if (minVertex > maxVertex) { minVertex = 0; maxVertex = 0; }
        int vertexCount = (int)(maxVertex - minVertex + 1);

        return new SubMeshData(
            BaseVertex: 0,
            FirstIndex: firstIndex,
            FirstVertex: (int)minVertex,
            IndexCount: indexCount,
            TriangleCount: indexCount / 3,
            VertexCount: vertexCount,
            Topology: MeshTopology.Triangles,
            LocalBounds: ComputeBounds(vertices, (int)minVertex, vertexCount));
    }

    private static Bounds ComputeBounds(SystemVector3[] vertices, int first, int count)
    {
        if (count <= 0 || first < 0 || first >= vertices.Length)
            return default;

        SystemVector3 min = vertices[first];
        SystemVector3 max = vertices[first];
        int end = Math.Min(first + count, vertices.Length);
        for (int i = first; i < end; i++)
        {
            min = SystemVector3.Min(min, vertices[i]);
            max = SystemVector3.Max(max, vertices[i]);
        }
        return new Bounds((min + max) * 0.5f, (max - min) * 0.5f);
    }
}
