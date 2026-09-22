using AssetRipper.Numerics;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse_Conversion.Dto;
using Ruri.RipperHook.Conversion;
using System.Numerics;

namespace Ruri.FModelHook.Unreal.Readers;

/// <summary>
/// One Unreal LOD restated as Unity geometry: every vertex stream through the basis, V flipped
/// (Unreal's V grows downward), and the triangle winding decided by the mesh's own normals --
/// the face normal of a triangle as Unity would wind it is compared with the vertices' stored
/// normals, and the majority says whether Unreal's order already faces out under Unity's
/// convention or has to be reversed. The data states it; nothing here assumes it.
/// </summary>
public static class UnrealMeshGeometry
{
    private const int WindingSampleLimit = 4096;
    private const int InfluenceLimit = 4;

    public static MeshGeometry FromLod<TVertex>(string name, MeshDto<TVertex> mesh, MeshLodDto<TVertex> lod, SourceBasis basis,
        Func<TVertex, MeshBoneInfluenceDto[]>? influences, Matrix4x4[]? bindPoses, string[]? boneNames, string? rootBoneName)
        where TVertex : struct, IMeshVertex
    {
        TVertex[] vertices = lod.Vertices;
        int count = vertices.Length;
        Vector3[] positions = new Vector3[count];
        Vector3[] normals = new Vector3[count];
        Vector4[] tangents = new Vector4[count];
        Vector2[] texCoords = new Vector2[count];
        bool anyNormal = false;
        bool anyTangent = false;
        for (int i = 0; i < count; i++)
        {
            TVertex vertex = vertices[i];
            FVector position = vertex.Position;
            positions[i] = basis.Position(position.X, position.Y, position.Z);
            FVector4 normal = vertex.Normal;
            Vector3 unityNormal = basis.Direction(normal.X, normal.Y, normal.Z);
            if (unityNormal.LengthSquared() > 0f)
            {
                anyNormal = true;
                unityNormal = Vector3.Normalize(unityNormal);
            }
            normals[i] = unityNormal;
            FVector4 tangent = vertex.Tangent;
            Vector3 unityTangent = basis.Direction(tangent.X, tangent.Y, tangent.Z);
            if (unityTangent.LengthSquared() > 0f)
            {
                anyTangent = true;
                unityTangent = Vector3.Normalize(unityTangent);
            }
            float sign = normal.W < 0f ? -1f : 1f;
            tangents[i] = new Vector4(unityTangent.X, unityTangent.Y, unityTangent.Z, sign);
            FMeshUVFloat uv = vertex.Uv;
            texCoords[i] = new Vector2(uv.U, 1f - uv.V);
        }

        Vector2[]?[] texCoordSets = new Vector2[]?[8];
        texCoordSets[0] = texCoords;
        for (int set = 0; set < lod.ExtraUvs.Length && set + 1 < texCoordSets.Length; set++)
        {
            FMeshUVFloat[] extra = lod.ExtraUvs[set];
            if (extra.Length != count)
            {
                continue;
            }
            Vector2[] converted = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                converted[i] = new Vector2(extra[i].U, 1f - extra[i].V);
            }
            texCoordSets[set + 1] = converted;
        }

        Vector4[]? colors = null;
        if (lod.VertexColors is { Length: > 0 } && lod.VertexColors[0].Colors.Length == count)
        {
            FColor[] source = lod.VertexColors[0].Colors;
            colors = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                FColor color = source[i];
                colors[i] = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
            }
        }

        uint[] indices = (uint[])lod.Indices.Clone();
        if (anyNormal && ShouldReverse(positions, normals, indices))
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        MeshSection[] sections = new MeshSection[lod.Sections.Length];
        for (int i = 0; i < sections.Length; i++)
        {
            MeshSectionDto section = lod.Sections[i];
            sections[i] = new MeshSection(section.FirstIndex, section.NumFaces * 3, section.MaterialIndex);
        }

        BoneWeight4[]? skin = null;
        if (influences is not null)
        {
            skin = new BoneWeight4[count];
            for (int i = 0; i < count; i++)
            {
                skin[i] = TopInfluences(influences(vertices[i]));
            }
        }

        return new MeshGeometry
        {
            Name = name,
            Positions = positions,
            Normals = anyNormal ? normals : null,
            Tangents = anyTangent ? tangents : null,
            Colors = colors,
            TexCoords = texCoordSets,
            Indices = indices,
            Sections = sections,
            Skin = skin,
            BindPoses = bindPoses,
            BoneNames = boneNames,
            RootBoneName = rootBoneName,
        };
    }

    private static bool ShouldReverse(Vector3[] positions, Vector3[] normals, uint[] indices)
    {
        int triangles = indices.Length / 3;
        if (triangles == 0)
        {
            return false;
        }
        int step = Math.Max(1, triangles / WindingSampleLimit);
        int agree = 0;
        int disagree = 0;
        for (int triangle = 0; triangle < triangles; triangle += step)
        {
            int at = triangle * 3;
            Vector3 a = positions[indices[at]];
            Vector3 b = positions[indices[at + 1]];
            Vector3 c = positions[indices[at + 2]];
            Vector3 face = Vector3.Cross(b - a, c - a);
            if (face.LengthSquared() <= 1e-18f)
            {
                continue;
            }
            Vector3 stored = normals[indices[at]] + normals[indices[at + 1]] + normals[indices[at + 2]];
            float dot = Vector3.Dot(face, stored);
            if (dot > 0f)
            {
                agree++;
            }
            else if (dot < 0f)
            {
                disagree++;
            }
        }
        return disagree > agree;
    }

    private static BoneWeight4 TopInfluences(MeshBoneInfluenceDto[] influences)
    {
        Span<int> indices = stackalloc int[InfluenceLimit];
        Span<float> weights = stackalloc float[InfluenceLimit];
        int kept = 0;
        foreach (MeshBoneInfluenceDto influence in influences)
        {
            float weight = influence.Weight;
            if (weight <= 0f)
            {
                continue;
            }
            int slot = kept < InfluenceLimit ? kept++ : -1;
            if (slot < 0)
            {
                int weakest = 0;
                for (int i = 1; i < InfluenceLimit; i++)
                {
                    if (weights[i] < weights[weakest])
                    {
                        weakest = i;
                    }
                }
                if (weights[weakest] >= weight)
                {
                    continue;
                }
                slot = weakest;
            }
            indices[slot] = influence.Bone;
            weights[slot] = weight;
        }
        float total = 0f;
        for (int i = 0; i < kept; i++)
        {
            total += weights[i];
        }
        if (total > 0f)
        {
            for (int i = 0; i < kept; i++)
            {
                weights[i] /= total;
            }
        }
        return new BoneWeight4(weights[0], weights[1], weights[2], weights[3], indices[0], indices[1], indices[2], indices[3]);
    }
}
