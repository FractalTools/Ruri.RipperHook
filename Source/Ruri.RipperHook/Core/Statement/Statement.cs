using System.Numerics;

namespace Ruri.RipperHook.Statements;

/// <summary>Everything one selection places, draws, wears and plays, in Unity's own space.
/// The tables a host reads are projections of this under a basis.</summary>
public sealed class Statement
{
    public List<StatementRoot> Roots { get; } = [];

    public List<StatementNode> Nodes { get; } = [];

    public List<StatementMesh> Meshes { get; } = [];

    public List<StatementSkeleton> Skeletons { get; } = [];

    public List<StatementMaterial> Materials { get; } = [];

    public List<StatementTexture> Textures { get; } = [];

    public List<StatementClip> Clips { get; } = [];

    public List<StatementMorph> Morphs { get; } = [];

    public List<StatementReport> Report { get; } = [];

    private readonly Dictionary<string, StatementMesh> _meshByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StatementMaterial> _materialByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StatementTexture> _textureByKey = new(StringComparer.Ordinal);

    public bool HasMesh(string key) => _meshByKey.ContainsKey(key);

    public bool HasMaterial(string key) => _materialByKey.ContainsKey(key);

    public bool HasTexture(string key) => _textureByKey.ContainsKey(key);

    public void Add(StatementMesh mesh)
    {
        _meshByKey[mesh.Key] = mesh;
        Meshes.Add(mesh);
    }

    public void Add(StatementMaterial material)
    {
        _materialByKey[material.Key] = material;
        Materials.Add(material);
    }

    public void Add(StatementTexture texture)
    {
        _textureByKey[texture.Key] = texture;
        Textures.Add(texture);
    }

    public void Note(string seed, string what, int count, string detail) =>
        Report.Add(new StatementReport(seed, what, count, detail));
}

/// <summary>One top-level thing a selection places.
///
/// <paramref name="Forward"/> is which way this source's OWN asset space faces, stated in the
/// engine's axes every decoded coordinate crosses in. It decides the once-only turn a host
/// puts on the top level, so the turn is the asset's own statement rather than a constant
/// anybody has to remember. Unity's transform forward is what an asset authored to be used
/// without a turn faces, and is what a source that states nothing gets.</summary>
public sealed record StatementRoot(string Seed, int Node, string Label, string Kind)
{
    public Vector3 Forward { get; init; } = Basis.UnityForward;
}

public sealed class StatementNode
{
    public required int Index { get; init; }

    public required int Parent { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Kind { get; set; }

    public required bool Active { get; init; }

    public string Mesh { get; set; } = string.Empty;

    public string Skeleton { get; set; } = string.Empty;

    public IReadOnlyList<string> Materials { get; set; } = [];

    /// <summary>The bone of its skeleton this node hangs on, when a title parents it to one
    /// rather than to a transform of the tree; the transform is then stated in the frame of
    /// the parent node, exactly as for any other node.</summary>
    public string Anchor { get; set; } = string.Empty;

    public required Vector3 Position { get; init; }

    public required Quaternion Rotation { get; init; }

    public required Vector3 Scale { get; init; }

    public UnityLightInfo? Light { get; set; }

    public UnityCameraInfo? Camera { get; set; }
}

public sealed class StatementMesh
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required DecodedMesh Geometry { get; init; }

    public IReadOnlyList<string> BonePaths { get; init; } = [];

    public string Skeleton { get; init; } = string.Empty;

    public int Lod { get; init; } = -1;

    public bool ShadowOnly { get; init; }

    public bool Baked { get; init; }
}

public sealed class StatementSkeleton
{
    public required string Key { get; init; }

    public List<StatementBone> Bones { get; } = [];

    public string AvatarJson { get; set; } = string.Empty;
}

public sealed record StatementBone(int Index, int Parent, string Name, string Path, string Identity,
    Vector3 Position, Quaternion Rotation, Vector3 Scale, string Humanoid);

public sealed class StatementMaterial
{
    public required string Key { get; init; }

    public required UnityMaterialProperties Properties { get; init; }

    public required TextureRoles.Resolution Roles { get; init; }
}

public sealed class StatementTexture
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required bool Srgb { get; init; }

    public required string Container { get; init; }

    public required byte[] Image { get; init; }
}

public sealed record StatementClip(string Key, string Name, string Skeleton, string MetaJson, byte[] Curves);

public sealed record StatementMorph(string Mesh, string Name, uint[] Vertices, float[] DeltaPositions,
    float[] DeltaNormals, float[] DeltaTangents, float Weight, int Frames);

public sealed record StatementReport(string Seed, string What, int Count, string Detail);
