using AssetRipper.Numerics;
using System.Numerics;

namespace Ruri.RipperHook.Conversion;

/// <summary>
/// Geometry already restated in Unity's basis (meters, Y up, Unity winding, V up), ready to
/// become a Mesh. One vertex per index slot: every optional stream is either null or exactly
/// <see cref="Positions"/> long. Bind poses are Unity's column-vector matrices, element
/// (row, column) at M(row+1)(column+1), so the translation sits in column 3.
/// </summary>
public sealed class MeshGeometry
{
    public required string Name { get; init; }

    public required Vector3[] Positions { get; init; }

    public Vector3[]? Normals { get; init; }

    public Vector4[]? Tangents { get; init; }

    public Vector4[]? Colors { get; init; }

    public Vector2[]?[] TexCoords { get; init; } = new Vector2[]?[8];

    public required uint[] Indices { get; init; }

    public required MeshSection[] Sections { get; init; }

    public BoneWeight4[]? Skin { get; init; }

    public Matrix4x4[]? BindPoses { get; init; }

    public string[]? BoneNames { get; init; }

    public string? RootBoneName { get; init; }

    public MeshMorph[] Morphs { get; init; } = [];
}

public readonly record struct MeshSection(int FirstIndex, int IndexCount, int MaterialIndex);

/// <summary>
/// One blend shape frame as sparse deltas: the vertices it moves, and per moved vertex the
/// position delta with optional normal and tangent deltas, all in Unity's basis.
/// </summary>
public sealed class MeshMorph
{
    public required string Name { get; init; }

    public required uint[] VertexIndices { get; init; }

    public required Vector3[] PositionDeltas { get; init; }

    public Vector3[]? NormalDeltas { get; init; }

    public Vector3[]? TangentDeltas { get; init; }
}
