using System.Numerics;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Writers.Gltf;
using CUE4Parse.UE4.Objects.Core.Math;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

using MESH = MeshBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>;

// Per-section glTF primitive build, moved from CUE4Parse-Conversion
// Writers/Gltf/Gltf.cs ExportMeshSections. Upstream walks a whole LOD in one
// private pass and names each primitive from the mesh's own material slot; the
// scene export needs one section at a time so a placement's override material
// can name the primitive instead.
public static class GlbMeshSectionBuilder
{
    // Gltf.cs UnitScale: centimetres to metres.
    private const float UnitScale = 0.01f;

    public static void AddSection(
        MESH meshBuilder,
        MeshLodDto<MeshVertex> lod,
        MeshSectionDto section,
        string materialName)
    {
        FColor[]? colors = null;
        if (lod.VertexColors is { Length: > 0 })
        {
            colors = lod.VertexColors[0].Colors;
        }

        int uvCount = 1 + lod.ExtraUvs.Length;
        var uvList1 = new Vector2[uvCount];
        var uvList2 = new Vector2[uvCount];
        var uvList3 = new Vector2[uvCount];

        var material = new MaterialBuilder().WithBaseColor(Vector4.One);
        material.Name = materialName;

        var primitive = meshBuilder.UsePrimitive(material);
        for (int faceIndex = 0; faceIndex < section.NumFaces; faceIndex++)
        {
            uint index0 = lod.Indices[section.FirstIndex + faceIndex * 3 + 0];
            uint index1 = lod.Indices[section.FirstIndex + faceIndex * 3 + 1];
            uint index2 = lod.Indices[section.FirstIndex + faceIndex * 3 + 2];

            MeshVertex vertex1 = lod.Vertices[index0];
            MeshVertex vertex2 = lod.Vertices[index1];
            MeshVertex vertex3 = lod.Vertices[index2];

            uvList1[0] = (Vector2)vertex1.Uv;
            uvList2[0] = (Vector2)vertex2.Uv;
            uvList3[0] = (Vector2)vertex3.Uv;
            for (int uvIndex = 0; uvIndex < lod.ExtraUvs.Length; uvIndex++)
            {
                uvList1[uvIndex + 1] = (Vector2)lod.ExtraUvs[uvIndex][index0];
                uvList2[uvIndex + 1] = (Vector2)lod.ExtraUvs[uvIndex][index1];
                uvList3[uvIndex + 1] = (Vector2)lod.ExtraUvs[uvIndex][index2];
            }

            primitive.AddTriangle(
                new VertexBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>(
                    ToVertex(vertex1), new VertexColorXTextureX(uvList1, colors?[index0])),
                new VertexBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>(
                    ToVertex(vertex2), new VertexColorXTextureX(uvList2, colors?[index1])),
                new VertexBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>(
                    ToVertex(vertex3), new VertexColorXTextureX(uvList3, colors?[index2])));
        }
    }

    // Gltf.cs PrepareTris, for a single vertex.
    private static VertexPositionNormalTangent ToVertex(MeshVertex vertex)
    {
        return new VertexPositionNormalTangent(
            Gltf.SwapYZ(vertex.Position * UnitScale),
            Gltf.SwapYZAndNormalize((FVector)vertex.Normal),
            Gltf.SwapYZAndNormalize((Vector4)vertex.Tangent));
    }
}
