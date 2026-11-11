using System.Numerics;
using AssetRipper.SourceGenerated.Enums;

namespace Ruri.RipperHook.BlenderBridge.Statements;

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

    public List<StatementAudioClip> AudioClips { get; } = [];

    public List<StatementController> Controllers { get; } = [];

    public List<StatementTimeline> Timelines { get; } = [];

    public List<StatementAnimator> Animators { get; } = [];

    public List<StatementCinemachine> Cinemachine { get; } = [];

    /// <summary>The components of the camera package this statement could not state, each with why. Asking for the components
    /// refuses with these, so that a scene is never exported with a camera missing; every other table is unaffected.</summary>
    public List<string> CinemachineRefused { get; } = [];

    public List<StatementScript> Scripts { get; } = [];

    /// <summary>The scripts this statement could not state, each with why. Asking for the scripts refuses with these, so that a
    /// scene is never exported with a component missing; every other table is unaffected.</summary>
    public List<string> ScriptsRefused { get; } = [];

    public List<StatementMorph> Morphs { get; } = [];

    public List<StatementCollider> Colliders { get; } = [];

    public List<StatementReport> Report { get; } = [];

    /// <summary>Static scene facts supplied by the plan's reader adapter, not runtime component behaviour.</summary>
    public List<string> SceneLighting { get; } = [];

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

    /// <summary>Whether the node draws right now: its object is active in the hierarchy and, for a
    /// node that carries a renderer, that renderer is enabled.</summary>
    public required bool Active { get; set; }

    /// <summary>Whether the node's own object is active (GameObject.activeSelf, serialized as m_IsActive), whatever its
    /// ancestors are: what SetActive writes and what a listener registers by once its ancestors are active. A node no object
    /// stands behind is active.</summary>
    public bool ActiveSelf { get; set; } = true;

    /// <summary>Whether the component this node's kind stands for (its renderer, light or camera) is itself enabled
    /// (Renderer.enabled, Behaviour.enabled, serialized as m_Enabled), whatever its object's activity is: what a script's
    /// enabled setter writes. A node with no such component is enabled.</summary>
    public bool ComponentEnabled { get; set; } = true;

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

    /// <summary>How the node's renderer draws into shadow maps, in Unity's own terms: off, on,
    /// two-sided, or into shadow maps and nothing else. It is the renderer's fact, not its mesh's --
    /// one mesh can be drawn by a renderer that casts and by one that does not. Null where the
    /// source states nothing: a node without a renderer, or a reader that does not read it.</summary>
    public required ShadowCastingMode? Shadows { get; set; }

    /// <summary>Whether a casting renderer's shadow falls in the directional light's cascades. A
    /// Unity renderer casts for every light; a streamed renderer can cast for local lights only.</summary>
    public bool MainLightShadows { get; set; } = true;

    /// <summary>Whether both faces of the renderer cast whatever its material's shadow culling says. Unity states this
    /// as <see cref="ShadowCastingMode.TwoSided"/>; a streamed renderer states it apart, shadows-only ones included.</summary>
    public bool TwoSidedShadows { get; set; }

    /// <summary>What the pipeline binds for this renderer's own draw beyond its materials: named
    /// four-component values, each under the name the pipeline's shaders read it by, that the engine
    /// sets per draw from the renderer itself rather than from any material. Empty where it binds none.</summary>
    public IReadOnlyList<KeyValuePair<string, Vector4>> ObjectParameters { get; set; } = [];

    /// <summary>The collection a host gathers this node under apart from the rest of what it imports, or empty
    /// for none. The pipeline's own draws are gathered by the shader that draws them: what a host renders them
    /// through (a layer of their own, a pass left out of the scene) is decided per collection.</summary>
    public string Collection { get; set; } = string.Empty;

    /// <summary>The engine's layer number of the object this node is, zero where the source states none.</summary>
    public int Layer { get; set; }

    /// <summary>The tag string of the object this node is, Untagged where the source states the default; empty where no object stands behind the node.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The key of the avatar the node's animator is built with, which makes the node the root of a rig;
    /// empty for a node that animates nothing.</summary>
    public string Avatar { get; set; } = string.Empty;

    public string SourceObject { get; set; } = string.Empty;

    public string SourceCollection { get; set; } = string.Empty;

    public int LightmapIndex { get; set; } = -1;

    public Vector4 LightmapST { get; set; } = new(1, 1, 0, 0);
}

public sealed class StatementMesh
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required DecodedMesh Geometry { get; init; }

    public IReadOnlyList<string> BonePaths { get; init; } = [];

    public string Skeleton { get; init; } = string.Empty;

    public int Lod { get; init; } = -1;


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

    /// <summary>The pixel format the source stores the texture in, by the source engine's own name (a Unity
    /// <c>TextureFormat</c>); empty where the source hands its pixels over already decoded.</summary>
    public required string Format { get; init; }

    public required byte[] Image { get; init; }

    /// <summary>How the texture's own sampler addresses it along u and v, and filters it
    /// (<see cref="TextureEncoding.Sampling"/>): what a shader that samples it through its own
    /// sampler gets, rather than a sampler state the shader states inline.</summary>
    public required TextureSampling Sampling { get; init; }
}

/// <summary>A collision volume on a node, in the node's own space: a box (centre, size), a sphere (centre, radius), a capsule
/// (centre, radius, height, axis) or a mesh (the key of one of the statement's meshes).</summary>
public sealed record StatementCollider(int Node, string Kind, bool Trigger, bool Enabled, int Layer, Vector3 Center,
    Vector3 Size, float Radius, float Height, int Axis, bool Convex, string Mesh, string Material);

public sealed record StatementClip(string Key, string Name, string MetaJson, byte[] Curves, string Archive);

/// <summary>An audio clip as the engine plays one: the channels, frequency and whole number of samples per channel its sound bank states,
/// the duration those make (the game's own timeline takes a clip's length as its sample count over its frequency), the length the clip
/// itself serializes in single precision, its compression format and the clip as a standard Ogg Vorbis file.</summary>
public sealed record StatementAudioClip(string Key, string Name, int Channels, int Frequency, long SampleCount, double Duration,
    double SerializedLength, string Format, byte[] Ogg, string Archive);

/// <summary>An animator controller as one JSON record: its parameters, layers, state machines, states, transitions,
/// conditions and blend trees, every clip it plays named by the key the clip statement states for it.</summary>
public sealed record StatementController(string Key, string Name, string RecordJson, string Archive);

/// <summary>A timeline asset as one JSON record -- its tracks, clips, markers and the playable asset each clip plays -- and, when
/// a director plays it, the scene path of that director and the object each track is bound to. The director is empty for an
/// asset no director of the selection plays.</summary>
public sealed record StatementTimeline(string Key, string Name, string Director, string RecordJson, string Archive);

/// <summary>An Animator component on a node: the controller it plays (by key, empty when none) and the avatar it was built with.
/// <paramref name="KeepControllerState"/> is whether the controller keeps its state when the object is disabled and enabled again
/// (the field the game's Unity version serializes as keep-animator-state-on-disable, or under its older name keep-animator-controller-state-on-disable);
/// false is the engine's default, where a re-enabled object starts the controller from its default state.
/// <paramref name="PathOrdinal"/> is how many nodes of <paramref name="Path"/> come before the node the Animator sits on in the
/// statement (the number a script record states as its pathOrdinal), which tells apart the Animators of two nodes that share a path.</summary>
public sealed record StatementAnimator(string Path, string Controller, string Avatar, bool Enabled, bool ApplyRootMotion,
    string UpdateMode, string CullingMode, bool KeepControllerState = false, int PathOrdinal = 0);

/// <summary>A component of the camera package's own scripts on a node: the node it sits on (its source path, the one the scene
/// layer states the node by), the class of its script, and the component as one JSON record -- whether it is enabled and every
/// field its script serializes, each under the name the script gives it.</summary>
public sealed record StatementCinemachine(string Path, string Class, string RecordJson);

/// <summary>A component of a node, or a scriptable object, as one JSON record: the archive it is read from, whether it is a
/// component or an asset, its key, the source path of the node it sits on (empty for an asset), its class, and every field its
/// script serializes under the name the script gives it.</summary>
public sealed record StatementScript(string Archive, string Kind, string Key, string Path, string Class, string RecordJson);

public sealed record StatementMorph(string Mesh, string Name, uint[] Vertices, float[] DeltaPositions,
    float[] DeltaNormals, float[] DeltaTangents, float Weight, int Frames);

public sealed record StatementReport(string Seed, string What, int Count, string Detail);
