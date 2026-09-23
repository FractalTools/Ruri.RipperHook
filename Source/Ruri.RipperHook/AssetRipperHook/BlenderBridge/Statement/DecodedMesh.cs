namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>Geometry decoded from a Unity mesh, in Unity's own coordinates, as flat
/// interleaved buffers: one vertex per index slot, every optional stream either null or
/// exactly <see cref="VertexCount"/> entries long.</summary>
public sealed class DecodedMesh
{
    public DecodedMesh(string name)
    {
        Name = name;
    }

    public string Name { get; set; }

    public int VertexCount { get; set; }

    public float[]? Positions { get; set; }

    public float[]? Normals { get; set; }

    public float[]? Tangents { get; set; }

    public float[]? Colors { get; set; }

    public SortedDictionary<int, float[]> Uvs { get; } = [];

    public int InfluenceCount { get; set; }

    public float[]? BoneWeights { get; set; }

    public int[]? BoneIndices { get; set; }

    public uint[] Triangles { get; set; } = [];

    public int[] TriangleMaterial { get; set; } = [];

    public List<DecodedSubMesh> SubMeshes { get; } = [];

    public float[]? BindPoses { get; set; }

    public uint[]? BoneNameHashes { get; set; }

    public List<DecodedBlendShape> BlendShapes { get; } = [];

    public long VariableBoneCountWeights { get; set; }

    public string Builtin { get; set; } = string.Empty;

    public int BindPoseCount => BindPoses is null ? 0 : BindPoses.Length / 16;

    public bool HasGeometry => Positions is not null && Positions.Length > 0;
}

public readonly record struct DecodedSubMesh(
    long FirstIndex, long IndexCount, int Topology, long BaseVertex, long FirstVertex, long VertexCount);

public sealed class DecodedBlendShape
{
    public required string Name { get; init; }

    public List<DecodedBlendShapeFrame> Frames { get; } = [];
}

/// <summary>One frame of a blend shape as sparse deltas: the moved vertices and, per moved
/// vertex, its position, normal and tangent delta.</summary>
public sealed class DecodedBlendShapeFrame
{
    public required float Weight { get; init; }

    public required bool HasNormals { get; init; }

    public required bool HasTangents { get; init; }

    public required uint[] VertexIndices { get; init; }

    public required float[] PositionDeltas { get; init; }

    public required float[] NormalDeltas { get; init; }

    public required float[] TangentDeltas { get; init; }
}
