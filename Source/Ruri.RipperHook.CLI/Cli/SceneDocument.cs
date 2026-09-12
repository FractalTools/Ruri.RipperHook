using System.Globalization;
using System.Text;
using AssetRipper.Export.UnityProjects;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// A Unity scene (.unity), written in the same serialized YAML Unity itself writes.
///
/// <para>This exists because a scene window's ARRANGEMENT has nowhere else to go. The
/// exporter writes every asset a window places — meshes, materials, textures, prefabs, each
/// with its own .meta and guid — but where each of those stands in the world is the game's
/// own streaming-chunk data, which no Unity asset carries. Without this the export is a pile
/// of correct assets and a JSON nothing reads.</para>
///
/// <para>Every reference is written by <see cref="MetaPtr.ToString"/> — the exporter's own
/// formatter for <c>{fileID, guid, type}</c> — so the pointers in this file and the pointers
/// in the assets beside it can never disagree about their spelling.</para>
///
/// <para>Two shapes, because a placement names two different kinds of thing. A MESH is
/// referenced: one GameObject carrying MeshFilter + MeshRenderer, with the placement's own
/// transform. A PREFAB is instantiated: a PrefabInstance whose modifications override the
/// prefab's root transform, which is the only form that keeps what the prefab itself
/// contains — its LODGroup, its particle systems, its own hierarchy — working as Unity
/// intends. Flattening a prefab into loose renderers (what the Blender importer must do,
/// having no LODGroup of its own) would here throw away geometry the target engine can
/// use.</para>
/// </summary>
internal sealed class SceneDocument
{
    internal readonly record struct Vec3(float X, float Y, float Z)
    {
        public static Vec3 One { get; } = new(1f, 1f, 1f);
        public static Vec3 Zero { get; } = new(0f, 0f, 0f);
    }

    internal readonly record struct Quat(float X, float Y, float Z, float W)
    {
        public static Quat Identity { get; } = new(0f, 0f, 0f, 1f);
    }

    // 1..4 are the scene's own settings documents; everything built here starts past them.
    private const long FirstId = 100;

    private sealed class Node
    {
        public long GameObjectId;
        public long TransformId;
        public string Name = "";
        public long ParentTransformId;
        public List<long>? Children;
        public Vec3 Position;
        public Quat Rotation;
        public Vec3 Scale;
        public long MeshFilterId;
        public long RendererId;
        public MetaPtr Mesh;
        public MetaPtr[] Materials = [];
    }

    private sealed class Instance
    {
        public long InstanceId;
        public long StrippedTransformId;
        public string Name = "";
        public long ParentTransformId;
        public Vec3 Position;
        public Quat Rotation;
        public Vec3 Scale;
        public MetaPtr SourcePrefab;
        public MetaPtr RootTransform;
    }

    private readonly List<Node> _nodes = new();
    private readonly List<Instance> _instances = new();
    private readonly Dictionary<long, Node> _byTransform = new();
    private long _nextId = FirstId;

    public SceneDocument(string rootName)
    {
        RootTransformId = AddGroup(rootName, 0);
    }

    /// <summary>The transform every placement hangs under, so the whole import moves as one.</summary>
    public long RootTransformId { get; }

    public int ObjectCount => _nodes.Count + _instances.Count;

    /// <summary>An empty GameObject: the scene root, and one per distinct asset so the
    /// hierarchy of a 10^4-placement window stays navigable instead of being one flat wall.</summary>
    public long AddGroup(string name, long parentTransformId)
        => AddNode(name, parentTransformId, Vec3.Zero, Quat.Identity, Vec3.One, null, []).TransformId;

    public long AddMeshObject(string name, long parentTransformId, Vec3 position, Quat rotation,
        Vec3 scale, MetaPtr mesh, IReadOnlyList<MetaPtr> materials)
        => AddNode(name, parentTransformId, position, rotation, scale, mesh, materials).TransformId;

    public void AddPrefabInstance(string name, long parentTransformId, Vec3 position, Quat rotation,
        Vec3 scale, MetaPtr sourcePrefab, MetaPtr rootTransform)
    {
        Instance instance = new()
        {
            InstanceId = _nextId++,
            StrippedTransformId = _nextId++,
            Name = name,
            ParentTransformId = parentTransformId,
            Position = position,
            Rotation = rotation,
            Scale = scale,
            SourcePrefab = sourcePrefab,
            RootTransform = rootTransform,
        };
        _instances.Add(instance);
        // A parented PrefabInstance is listed among its parent's children through a STRIPPED
        // transform standing in for the prefab's own root -- the form Unity writes, and the
        // reason the root transform's pointer had to be captured at export time.
        AddChild(parentTransformId, instance.StrippedTransformId);
    }

    private Node AddNode(string name, long parentTransformId, Vec3 position, Quat rotation,
        Vec3 scale, MetaPtr? mesh, IReadOnlyList<MetaPtr> materials)
    {
        Node node = new()
        {
            GameObjectId = _nextId++,
            TransformId = _nextId++,
            Name = name,
            ParentTransformId = parentTransformId,
            Position = position,
            Rotation = rotation,
            Scale = scale,
        };
        if (mesh is MetaPtr meshPointer)
        {
            node.MeshFilterId = _nextId++;
            node.RendererId = _nextId++;
            node.Mesh = meshPointer;
            node.Materials = materials as MetaPtr[] ?? materials.ToArray();
        }
        _nodes.Add(node);
        _byTransform[node.TransformId] = node;
        AddChild(parentTransformId, node.TransformId);
        return node;
    }

    private void AddChild(long parentTransformId, long childTransformId)
    {
        if (parentTransformId != 0 && _byTransform.TryGetValue(parentTransformId, out Node? parent))
        {
            (parent.Children ??= new List<long>()).Add(childTransformId);
        }
    }

    public string Build()
    {
        StringBuilder text = new(1024 * 1024);
        text.Append(SettingsDocuments);
        foreach (Node node in _nodes)
        {
            WriteNode(text, node);
        }
        foreach (Instance instance in _instances)
        {
            WriteInstance(text, instance);
        }
        return text.ToString();
    }

    private static void WriteNode(StringBuilder text, Node node)
    {
        text.Append("--- !u!1 &").Append(node.GameObjectId).Append('\n');
        text.Append("GameObject:\n");
        text.Append("  m_ObjectHideFlags: 0\n");
        text.Append("  m_CorrespondingSourceObject: {fileID: 0}\n");
        text.Append("  m_PrefabInstance: {fileID: 0}\n");
        text.Append("  m_PrefabAsset: {fileID: 0}\n");
        text.Append("  serializedVersion: 6\n");
        text.Append("  m_Component:\n");
        text.Append("  - component: {fileID: ").Append(node.TransformId).Append("}\n");
        if (node.MeshFilterId != 0)
        {
            text.Append("  - component: {fileID: ").Append(node.MeshFilterId).Append("}\n");
            text.Append("  - component: {fileID: ").Append(node.RendererId).Append("}\n");
        }
        text.Append("  m_Layer: 0\n");
        text.Append("  m_Name: ").Append(Scalar(node.Name)).Append('\n');
        text.Append("  m_TagString: Untagged\n");
        text.Append("  m_Icon: {fileID: 0}\n");
        text.Append("  m_NavMeshLayer: 0\n");
        text.Append("  m_StaticEditorFlags: 0\n");
        text.Append("  m_IsActive: 1\n");

        text.Append("--- !u!4 &").Append(node.TransformId).Append('\n');
        text.Append("Transform:\n");
        text.Append("  m_ObjectHideFlags: 0\n");
        text.Append("  m_CorrespondingSourceObject: {fileID: 0}\n");
        text.Append("  m_PrefabInstance: {fileID: 0}\n");
        text.Append("  m_PrefabAsset: {fileID: 0}\n");
        text.Append("  m_GameObject: {fileID: ").Append(node.GameObjectId).Append("}\n");
        text.Append("  serializedVersion: 2\n");
        text.Append("  m_LocalRotation: ").Append(Rotation(node.Rotation)).Append('\n');
        text.Append("  m_LocalPosition: ").Append(Position(node.Position)).Append('\n');
        text.Append("  m_LocalScale: ").Append(Position(node.Scale)).Append('\n');
        text.Append("  m_Children:");
        if (node.Children is null)
        {
            text.Append(" []\n");
        }
        else
        {
            text.Append('\n');
            foreach (long child in node.Children)
            {
                text.Append("  - {fileID: ").Append(child).Append("}\n");
            }
        }
        text.Append("  m_Father: {fileID: ").Append(node.ParentTransformId).Append("}\n");
        text.Append("  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n");

        if (node.MeshFilterId == 0)
        {
            return;
        }

        text.Append("--- !u!33 &").Append(node.MeshFilterId).Append('\n');
        text.Append("MeshFilter:\n");
        text.Append("  m_ObjectHideFlags: 0\n");
        text.Append("  m_CorrespondingSourceObject: {fileID: 0}\n");
        text.Append("  m_PrefabInstance: {fileID: 0}\n");
        text.Append("  m_PrefabAsset: {fileID: 0}\n");
        text.Append("  m_GameObject: {fileID: ").Append(node.GameObjectId).Append("}\n");
        text.Append("  m_Mesh: ").Append(node.Mesh.ToString()).Append('\n');

        text.Append("--- !u!23 &").Append(node.RendererId).Append('\n');
        text.Append("MeshRenderer:\n");
        text.Append("  m_ObjectHideFlags: 0\n");
        text.Append("  m_CorrespondingSourceObject: {fileID: 0}\n");
        text.Append("  m_PrefabInstance: {fileID: 0}\n");
        text.Append("  m_PrefabAsset: {fileID: 0}\n");
        text.Append("  m_GameObject: {fileID: ").Append(node.GameObjectId).Append("}\n");
        // Every one of these is written out rather than left to default: a field Unity does
        // not find in the document takes the TYPE's default, and for a renderer that means
        // m_Enabled 0 (drawn by nothing) and m_CastShadows 0 (no shadows) -- a silently
        // different scene, not a missing detail.
        text.Append("  m_Enabled: 1\n");
        text.Append("  m_CastShadows: 1\n");
        text.Append("  m_ReceiveShadows: 1\n");
        text.Append("  m_DynamicOccludee: 1\n");
        text.Append("  m_StaticShadowCaster: 0\n");
        text.Append("  m_MotionVectors: 1\n");
        text.Append("  m_LightProbeUsage: 1\n");
        text.Append("  m_ReflectionProbeUsage: 1\n");
        text.Append("  m_RayTracingMode: 2\n");
        text.Append("  m_RayTraceProcedural: 0\n");
        text.Append("  m_RenderingLayerMask: 1\n");
        text.Append("  m_RendererPriority: 0\n");
        if (node.Materials.Length == 0)
        {
            text.Append("  m_Materials: []\n");
        }
        else
        {
            text.Append("  m_Materials:\n");
            foreach (MetaPtr material in node.Materials)
            {
                text.Append("  - ").Append(material.ToString()).Append('\n');
            }
        }
        text.Append("  m_StaticBatchInfo:\n");
        text.Append("    firstSubMesh: 0\n");
        text.Append("    subMeshCount: 0\n");
        text.Append("  m_StaticBatchRoot: {fileID: 0}\n");
        text.Append("  m_ProbeAnchor: {fileID: 0}\n");
        text.Append("  m_LightProbeVolumeOverride: {fileID: 0}\n");
        text.Append("  m_ScaleInLightmap: 1\n");
        text.Append("  m_ReceiveGI: 1\n");
        text.Append("  m_PreserveUVs: 0\n");
        text.Append("  m_IgnoreNormalsForChartDetection: 0\n");
        text.Append("  m_ImportantGI: 0\n");
        text.Append("  m_StitchLightmapSeams: 1\n");
        text.Append("  m_SelectedEditorRenderState: 3\n");
        text.Append("  m_MinimumChartSize: 4\n");
        text.Append("  m_AutoUVMaxDistance: 0.5\n");
        text.Append("  m_AutoUVMaxAngle: 89\n");
        text.Append("  m_LightmapParameters: {fileID: 0}\n");
        text.Append("  m_SortingLayerID: 0\n");
        text.Append("  m_SortingLayer: 0\n");
        text.Append("  m_SortingOrder: 0\n");
        text.Append("  m_AdditionalVertexStreams: {fileID: 0}\n");
    }

    private static void WriteInstance(StringBuilder text, Instance instance)
    {
        string target = instance.RootTransform.ToString();
        text.Append("--- !u!1001 &").Append(instance.InstanceId).Append('\n');
        text.Append("PrefabInstance:\n");
        text.Append("  m_ObjectHideFlags: 0\n");
        text.Append("  serializedVersion: 2\n");
        text.Append("  m_Modification:\n");
        text.Append("    serializedVersion: 3\n");
        text.Append("    m_TransformParent: {fileID: ").Append(instance.ParentTransformId).Append("}\n");
        text.Append("    m_Modifications:\n");
        Modification(text, target, "m_LocalPosition.x", instance.Position.X);
        Modification(text, target, "m_LocalPosition.y", instance.Position.Y);
        Modification(text, target, "m_LocalPosition.z", instance.Position.Z);
        Modification(text, target, "m_LocalRotation.x", instance.Rotation.X);
        Modification(text, target, "m_LocalRotation.y", instance.Rotation.Y);
        Modification(text, target, "m_LocalRotation.z", instance.Rotation.Z);
        Modification(text, target, "m_LocalRotation.w", instance.Rotation.W);
        Modification(text, target, "m_LocalScale.x", instance.Scale.X);
        Modification(text, target, "m_LocalScale.y", instance.Scale.Y);
        Modification(text, target, "m_LocalScale.z", instance.Scale.Z);
        // Unity recomputes the euler hint from the quaternion, but leaving the prefab's own
        // hint in place makes the inspector show a rotation the transform does not have.
        Modification(text, target, "m_LocalEulerAnglesHint.x", 0f);
        Modification(text, target, "m_LocalEulerAnglesHint.y", 0f);
        Modification(text, target, "m_LocalEulerAnglesHint.z", 0f);
        text.Append("    m_RemovedComponents: []\n");
        text.Append("    m_RemovedGameObjects: []\n");
        text.Append("    m_AddedGameObjects: []\n");
        text.Append("    m_AddedComponents: []\n");
        text.Append("  m_SourcePrefab: ").Append(instance.SourcePrefab.ToString()).Append('\n');

        text.Append("--- !u!4 &").Append(instance.StrippedTransformId).Append(" stripped\n");
        text.Append("Transform:\n");
        text.Append("  m_CorrespondingSourceObject: ").Append(target).Append('\n');
        text.Append("  m_PrefabInstance: {fileID: ").Append(instance.InstanceId).Append("}\n");
        text.Append("  m_PrefabAsset: {fileID: 0}\n");
    }

    private static void Modification(StringBuilder text, string target, string property, float value)
    {
        text.Append("    - target: ").Append(target).Append('\n');
        text.Append("      propertyPath: ").Append(property).Append('\n');
        text.Append("      value: ").Append(Number(value)).Append('\n');
        text.Append("      objectReference: {fileID: 0}\n");
    }

    private static string Position(Vec3 value) =>
        $"{{x: {Number(value.X)}, y: {Number(value.Y)}, z: {Number(value.Z)}}}";

    private static string Rotation(Quat value) =>
        $"{{x: {Number(value.X)}, y: {Number(value.Y)}, z: {Number(value.Z)}, w: {Number(value.W)}}}";

    private static string Number(float value)
    {
        // A non-finite coordinate is not a number Unity can read; a scene that carries one
        // fails to open at all, so it lands at the origin and stays visible instead.
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return "0";
        }
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A name as YAML. Quoted only where the plain form would parse as something else --
    /// which is what Unity itself does, so a name that needs no quoting reads identically
    /// to one Unity wrote.
    /// </summary>
    private static string Scalar(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }
        bool needsQuotes = char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || "-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) >= 0
            || value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || value.EndsWith(':')
            || value.Any(char.IsControl);
        return needsQuotes ? "'" + value.Replace("'", "''") + "'" : value;
    }

    /// <summary>
    /// The four documents every scene carries. Kept deliberately short: a field Unity does
    /// not find takes the type's own default, so only the settings whose default would be
    /// WRONG are stated. The one that matters is <c>m_AmbientMode: 3</c> (a flat ambient
    /// colour) -- the default, Skybox, resolves against the skybox material this scene has
    /// none of, and lights everything pure black.
    /// </summary>
    private const string SettingsDocuments = """
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!29 &1
OcclusionCullingSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_OcclusionBakeSettings:
    smallestOccluder: 5
    smallestHole: 0.25
    backfaceThreshold: 100
  m_SceneGUID: 00000000000000000000000000000000
  m_OcclusionCullingData: {fileID: 0}
--- !u!104 &2
RenderSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 9
  m_Fog: 0
  m_FogColor: {r: 0.5, g: 0.5, b: 0.5, a: 1}
  m_FogMode: 3
  m_FogDensity: 0.01
  m_LinearFogStart: 0
  m_LinearFogEnd: 300
  m_AmbientSkyColor: {r: 0.5, g: 0.5, b: 0.5, a: 1}
  m_AmbientEquatorColor: {r: 0.4, g: 0.4, b: 0.4, a: 1}
  m_AmbientGroundColor: {r: 0.3, g: 0.3, b: 0.3, a: 1}
  m_AmbientIntensity: 1
  m_AmbientMode: 3
  m_SkyboxMaterial: {fileID: 0}
  m_HaloStrength: 0.5
  m_FlareStrength: 1
  m_FlareFadeSpeed: 3
  m_HaloTexture: {fileID: 0}
  m_SpotCookie: {fileID: 10001, guid: 0000000000000000e000000000000000, type: 0}
  m_DefaultReflectionMode: 0
  m_DefaultReflectionResolution: 128
  m_ReflectionBounces: 1
  m_ReflectionIntensity: 1
  m_CustomReflection: {fileID: 0}
  m_Sun: {fileID: 0}
  m_UseRadianceAmbientProbe: 0
--- !u!157 &3
LightmapSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 12
  m_GIWorkflowMode: 1
  m_LightingSettings: {fileID: 0}
  m_LightmapsMode: 1
  m_LightingDataAsset: {fileID: 0}
  m_UseShadowmask: 1
--- !u!196 &4
NavMeshSettings:
  serializedVersion: 2
  m_ObjectHideFlags: 0
  m_BuildSettings:
    serializedVersion: 3
    agentTypeID: 0
    agentRadius: 0.5
    agentHeight: 2
    agentSlope: 45
    agentClimb: 0.4
    ledgeDropHeight: 0
    maxJumpAcrossDistance: 0
    minRegionArea: 2
    manualCellSize: 0
    cellSize: 0.16666667
    manualTileSize: 0
    tileSize: 256
    buildHeightMesh: 0
    maxJobWorkers: 0
    preserveTilesOutsideBounds: 0
    debug:
      m_Flags: 0
  m_NavMeshData: {fileID: 0}

""";
}
