using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.glTF;
using CUE4Parse_Conversion.Meshes.PSK;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

using MESH = MeshBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>;

// Shared scene-building services every IComponentExporter writes through.
//
// The previous monolithic WorldGlbExporter owned three concerns in one class:
//   (1) iterate actors and pick the right component family;
//   (2) build the SharpGLTF SceneBuilder + share meshes across instances;
//   (3) flush in budgeted parts and stitch the final files.
// (1) now lives in ComponentResolver + the per-family IComponentExporter
// table. (2) and (3) move here so every exporter writes into ONE scene and
// the part-flush cadence stays bounded by the same instance budget regardless
// of which families show up.
//
// Mesh-sharing key (critical correctness fix):
// the verified FModel Renderer caches by `mesh.LightingGuid` and then RUN-PATCHES
// the cached model's materials via `OverrideMaterials` per placement
// (Renderer.cs:642-652). The old port shared by LightingGuid only, so the first
// caller's material set won the cache and every subsequent placement that
// carried OverrideMaterials inherited the wrong materials — the
// `_overrideMaterialSkips` counter in the original implementation logged
// exactly that bug. Since we cannot mutate the shared MeshBuilder per-
// placement, the only correct fix is to fold the override material set into
// the cache key: identical mesh + identical material override list = share;
// any difference = a fresh MeshBuilder. That keeps the byte-for-byte equality
// with `Gltf.ExportStaticMeshSections` and still instances heavily when
// nothing overrides.
//
// Part flushing: SharpGLTF's `ToGltf2()` materialises one Schema2 node per
// AddRigidMesh, and a UE5 open world expands InstancedStaticMeshComponent
// into hundreds of thousands of placements — over a budget the resulting
// ModelRoot exhausts memory. So when the in-flight scene has accumulated
// MaxInstancesPerGlb nodes the context flushes to "<base>.partNNN.glb" and
// starts a fresh SceneBuilder; the part counter is shared across families so
// a mixed scene still falls inside one budgeted file when it fits.
public sealed class GlbSceneContext
{
    // Node budget per .glb part. Kept well under the point where ToGltf2()
    // exhausts memory on a large open world, while keeping the part count low.
    public const int MaxInstancesPerGlb = 50_000;

    public IFileProvider Provider { get; }
    public ExporterOptions Options { get; }
    public Action<string> Log { get; }
    public Action<string> LogError { get; }
    public GlbMaterialFactory MaterialFactory { get; }
    public SceneManifest Manifest { get; }

    // Reset on every part flush so peak memory is bounded by one part. Each
    // mesh is cached against (LightingGuid + ordered override material path
    // list) so a placement that overrides materials does NOT collide with a
    // base placement that left them alone. See class doc.
    private SceneBuilder _sceneBuilder = new();
    private readonly Dictionary<MeshShareKey, MESH?> _meshCache = new();

    // Persist across parts: materials are written once (deduped) and the
    // distinct mesh / part bookkeeping is for the summary log.
    private readonly List<MaterialExporter2> _materialExporters = new();
    private readonly List<string> _materialKeys = new();
    private readonly HashSet<string> _writtenMaterialKeys = new(StringComparer.Ordinal);
    private readonly HashSet<MeshShareKey> _distinctMeshKeys = new();
    private readonly List<string> _writtenParts = new();

    // Punctual lights collected across the whole render pass. In this SharpGLTF
    // build the SceneBuilder LightBuilder.Point/Spot/Directional constructors are
    // INTERNAL — there is no public way to hand a light to SceneBuilder.AddLight.
    // So lights are buffered here and emitted at the Schema2 layer in a dedicated
    // final part (WritePendingLights) after every mesh part is flushed. They do
    // not participate in the instance budget (a few hundred lights vs hundreds of
    // thousands of mesh placements).
    private readonly List<PendingLight> _pendingLights = new();

    // Cameras have the SAME problem as lights in this SharpGLTF build:
    // SceneBuilder.AddCamera is a silent no-op (ToGltf2 emits zero LogicalCameras
    // — verified by probe). So cameras are buffered too and emitted at the Schema2
    // layer alongside the lights in the dedicated final part.
    private readonly List<PendingCamera> _pendingCameras = new();

    private string _outputBasePath = string.Empty;
    private int _placementCount;
    private int _batchInstanceCount;

    public GlbSceneContext(
        IFileProvider provider,
        ExporterOptions options,
        Action<string> log,
        Action<string> logError,
        GlbMaterialFactory materialFactory,
        SceneManifest manifest)
    {
        Provider = provider;
        Options = options;
        Log = log;
        LogError = logError;
        MaterialFactory = materialFactory;
        Manifest = manifest;
    }

    public int PlacementCount => _placementCount;
    public int UniqueMeshCount => _distinctMeshKeys.Count;
    public int MaterialCount => _materialExporters.Count;
    public IReadOnlyList<string> WrittenParts => _writtenParts;
    public IReadOnlyList<MaterialExporter2> MaterialExporters => _materialExporters;
    public IReadOnlyList<string> MaterialKeys => _materialKeys;
    public string OutputBasePath => _outputBasePath;

    public void SetOutputBasePath(string outputBasePath)
    {
        _outputBasePath = outputBasePath;
    }

    // Append a static-mesh placement to the in-flight scene. Returns true if
    // the call added a node (false = mesh failed to build, e.g. no LODs).
    //
    // `overrideMaterials` is the per-section override array the verified
    // Renderer reads off the component (Renderer.cs:642-652), already loaded
    // by the caller. Nulls in the array mean "this slot keeps the section's
    // base material". The path-name list passed in tandem is precomputed by
    // the caller and used as the cache key — so identical (mesh, override
    // list) tuples share a single MeshBuilder, while any different override
    // list builds a fresh one. This is the gotcha-list-cited fix for the
    // write/read key inconsistency: cache write and lookup use the *same*
    // formula because the caller pre-computes the path-name list once.
    public bool AddRigidMesh(
        UStaticMesh mesh,
        IReadOnlyList<UMaterialInterface?> overrideMaterials,
        IReadOnlyList<string> overrideMaterialPathNames,
        Matrix4x4 nodeMatrix)
    {
        MeshShareKey key = new(mesh.LightingGuid, overrideMaterialPathNames);
        if (!_meshCache.TryGetValue(key, out var meshBuilder))
        {
            meshBuilder = BuildMesh(mesh, overrideMaterials);
            _meshCache[key] = meshBuilder;
        }
        if (meshBuilder == null)
        {
            // BuildMesh failed — the mesh has no LODs / no sections. Mirror
            // the verified Renderer behaviour (`if (meshBuilder == null) return;`
            // in OLD AddMeshInstance): swallow the placement silently. The
            // BuildMesh path already wrote its own [GlbScene] log entry naming
            // the mesh, so the audit trail is intact. We do NOT record this
            // as manifest.dropped because (a) per-instance placements off an
            // ISM with a null-mesh would spam thousands of identical entries,
            // and (b) the lossless layer already captures the owning actor's
            // full property tree, so the data is still in the package even
            // if the geometry isn't.
            return false;
        }

        _distinctMeshKeys.Add(key);
        _sceneBuilder.AddRigidMesh(meshBuilder, nodeMatrix);
        _placementCount++;
        _batchInstanceCount++;

        if (_batchInstanceCount >= MaxInstancesPerGlb)
        {
            FlushBatch();
        }
        return true;
    }

    // Buffer one punctual light. The LightComponentExporter computes the glTF
    // KHR_lights_punctual parameters (linear color, candela/lux intensity, range
    // in metres, spot cone half-angles in radians) plus the world node matrix
    // and hands them here; emission happens at the Schema2 layer in
    // WritePendingLights. `extrasJson`, when non-empty, is attached to the light
    // node so fallback families (Rect-as-Spot, Sky-as-Point) keep an audit note
    // on the node itself; the full per-byte light data still lives in the
    // lossless Actors/ JSON regardless.
    public void AddLight(
        PunctualLightType lightType,
        Vector3 color,
        float intensity,
        float range,
        float innerConeRadians,
        float outerConeRadians,
        string name,
        Matrix4x4 nodeMatrix,
        string? extrasJson = null)
    {
        _pendingLights.Add(new PendingLight(
            lightType, color, intensity, range, innerConeRadians, outerConeRadians, name, nodeMatrix, extrasJson));
    }

    public int PendingLightCount => _pendingLights.Count;
    public int PendingCameraCount => _pendingCameras.Count;

    // Buffer one camera. CameraBuilder.Perspective/Orthographic ARE publicly
    // constructable (so the exporter keeps doing its filmback/FOV math through
    // them), but SceneBuilder.AddCamera does not survive ToGltf2 in this build,
    // so we read the builder's resolved parameters here and re-emit at the
    // Schema2 layer in WritePendingLightsAndCameras. `name` gives the camera node
    // a meaningful identifier (the owning component's name).
    public void AddCamera(CameraBuilder camera, Matrix4x4 nodeMatrix, string name)
    {
        switch (camera)
        {
            case CameraBuilder.Perspective perspective:
                _pendingCameras.Add(PendingCamera.CreatePerspective(
                    perspective.AspectRatio, perspective.VerticalFOV, perspective.ZNear, perspective.ZFar, name, nodeMatrix));
                break;
            case CameraBuilder.Orthographic orthographic:
                _pendingCameras.Add(PendingCamera.CreateOrthographic(
                    orthographic.XMag, orthographic.YMag, orthographic.ZNear, orthographic.ZFar, name, nodeMatrix));
                break;
        }
    }

    // Emit every buffered punctual light into a dedicated final .glb part at the
    // Schema2 layer (one node per light carrying a PunctualLight). Called by
    // WorldGlbExporter AFTER the mesh render pass + final FlushBatch, so the
    // light set is complete and lands in exactly one file. Validated end-to-end:
    // an empty SceneBuilder.ToGltf2() yields a model with a default scene, and
    // ModelRoot.CreatePunctualLight + Node.PunctualLight produce a glb whose JSON
    // chunk carries the KHR_lights_punctual extension.
    public void WritePendingLightsAndCameras()
    {
        if (_pendingLights.Count == 0 && _pendingCameras.Count == 0) return;
        try
        {
            ModelRoot model = new SceneBuilder().ToGltf2();
            Scene scene = model.DefaultScene ?? model.UseScene(0);
            foreach (PendingCamera camera in _pendingCameras)
            {
                Camera schemaCamera = model.CreateCamera(camera.Name);
                if (camera.IsOrthographic)
                {
                    schemaCamera.SetOrthographicMode(camera.XMag, camera.YMag, camera.ZNear, camera.ZFar);
                }
                else
                {
                    schemaCamera.SetPerspectiveMode(camera.AspectRatio, camera.VerticalFov, camera.ZNear, camera.ZFar);
                }
                Node cameraNode = scene.CreateNode(camera.Name);
                cameraNode.WorldMatrix = camera.NodeMatrix;
                cameraNode.Camera = schemaCamera;
            }
            foreach (PendingLight light in _pendingLights)
            {
                PunctualLight punctual = model.CreatePunctualLight(light.Name, light.LightType);
                punctual.Color = light.Color;
                punctual.Intensity = light.Intensity;
                // glTF range applies to point/spot only; directional ignores it.
                if (light.Range > 0.0f && light.LightType != PunctualLightType.Directional)
                {
                    punctual.Range = light.Range;
                }
                if (light.LightType == PunctualLightType.Spot)
                {
                    punctual.SetSpotCone(light.InnerConeRadians, light.OuterConeRadians);
                }

                Node node = scene.CreateNode(light.Name);
                node.WorldMatrix = light.NodeMatrix;
                node.PunctualLight = punctual;
                if (!string.IsNullOrEmpty(light.ExtrasJson))
                {
                    node.Extras = SharpGLTF.IO.JsonContent.Parse(light.ExtrasJson);
                }
            }

            string partPath = $"{_outputBasePath}.part{_writtenParts.Count:D3}.glb";
            Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
            var glb = model.WriteGLB();
            using var stream = File.Create(partPath);
            stream.Write(glb.Array!, glb.Offset, glb.Count);
            _writtenParts.Add(partPath);
            Log($"[GlbScene] Wrote lights/cameras part ({_pendingLights.Count} punctual lights, {_pendingCameras.Count} cameras) -> {partPath}");
        }
        catch (Exception ex)
        {
            LogError($"[GlbScene] Lights/cameras part write failed: {ex.Message}");
        }
    }

    // Flush the in-flight SceneBuilder to disk. Called by WorldGlbExporter at
    // the end of the render pass; also called internally when the per-part
    // node budget is reached.
    public void FlushBatch()
    {
        if (_batchInstanceCount == 0) return;

        string partPath = $"{_outputBasePath}.part{_writtenParts.Count:D3}.glb";
        if (WriteSceneTo(partPath, _sceneBuilder))
        {
            _writtenParts.Add(partPath);
            Log($"[GlbScene] Wrote part {_writtenParts.Count - 1} ({_batchInstanceCount} instances) -> {partPath}");
        }

        _sceneBuilder = new SceneBuilder();
        _meshCache.Clear();
        _batchInstanceCount = 0;
    }

    // Single-mesh build: 1:1 port of the old WorldGlbExporter.BuildMesh path
    // plus the override-material substitution at material-name level so each
    // distinct (mesh, overrides) tuple emits a distinct MeshBuilder. Geometry
    // bytes are still produced by Gltf.ExportStaticMeshSections, so vertex/
    // index/UV bytes remain identical to FModel's "Save Model" output.
    //
    // The override-material substitution mirrors the verified Renderer path
    // (Renderer.cs:642-652): index by `section.MaterialIndex`, bounds-check
    // against the override array (and the section count), fall through if
    // the cell is null or not a UMaterialInterface — in which case the
    // section keeps its base material so SharpGLTF still gets a sensible
    // material name.
    private MESH? BuildMesh(UStaticMesh mesh, IReadOnlyList<UMaterialInterface?> overrideMaterials)
    {
        if (!mesh.TryConvert(out var convertedMesh, Options.NaniteMeshFormat) || convertedMesh.LODs.Count == 0)
        {
            LogError($"[GlbScene] Mesh '{mesh.Name}' has no LODs; skipped.");
            return null;
        }

        var lod = convertedMesh.LODs[0];
        if (lod.Sections.Value == null)
        {
            LogError($"[GlbScene] Mesh '{mesh.Name}' LOD0 has no sections; skipped.");
            return null;
        }

        var meshBuilder = new MESH(mesh.Name);
        var sections = lod.Sections.Value;
        int meshMaterialSlotCount = mesh.Materials.Length;
        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            CMeshSection baseSection = sections[sectionIndex];

            // 1:1 of Renderer.cs:646-650: matIndex must fit inside the mesh's
            // Materials array AND the override array. The earlier port keyed
            // the upper bound on the section count, which is divergent — a
            // mesh with more material slots than sections (common for atlas-
            // packed meshes) would skip valid overrides. Use the mesh's
            // Materials.Length as the renderer does.
            UMaterialInterface? overrideMaterial = ResolveOverrideMaterial(
                baseSection.MaterialIndex,
                meshMaterialSlotCount,
                overrideMaterials);

            // When the placement carries an override material at this section,
            // we synthesize a CMeshSection with the override's ResolvedObject in
            // the Material slot — same FirstIndex/NumFaces, same MaterialIndex,
            // so Gltf.ExportStaticMeshSections walks identical triangles but
            // emits the override material's name on the primitive. When there
            // is no override we hand the original section in unchanged.
            CMeshSection effectiveSection = overrideMaterial != null
                ? new CMeshSection(
                    baseSection.MaterialIndex,
                    baseSection.FirstIndex,
                    baseSection.NumFaces,
                    overrideMaterial.Name,
                    new ResolvedLoadedObject(overrideMaterial))
                : baseSection;

            string? materialKey = effectiveSection.Material?.Load<UMaterialInterface>()?.GetPathName();

            int before = Options.ExportMaterials ? _materialExporters.Count : 0;
            Gltf.ExportStaticMeshSections(
                sectionIndex,
                lod,
                effectiveSection,
                Options.ExportMaterials ? _materialExporters : null,
                meshBuilder,
                Options);

            // De-duplicate the material exporter just appended so a material
            // shared across the scene is decoded and written only once (even
            // though its geometry is re-emitted into each part that uses it).
            if (Options.ExportMaterials && _materialExporters.Count > before)
            {
                if (materialKey == null || !_writtenMaterialKeys.Add(materialKey))
                {
                    _materialExporters.RemoveRange(before, _materialExporters.Count - before);
                }
                else
                {
                    _materialKeys.Add(materialKey);
                }
            }
        }
        return meshBuilder;
    }

    // 1:1 of Renderer.cs:646-650 selection: bounds-check matIndex against
    // both the mesh's Materials slot count AND the override array length,
    // then read the override slot. Renderer.cs returns the slot loaded as
    // UMaterialInterface or skips; here we pre-loaded so the entry is
    // already a UMaterialInterface or null.
    private static UMaterialInterface? ResolveOverrideMaterial(
        int materialIndex,
        int meshMaterialSlotCount,
        IReadOnlyList<UMaterialInterface?> overrideMaterials)
    {
        if (overrideMaterials.Count == 0) return null;
        if (materialIndex < 0 || materialIndex >= overrideMaterials.Count) return null;
        if (materialIndex >= meshMaterialSlotCount) return null;
        return overrideMaterials[materialIndex];
    }

    private bool WriteSceneTo(string glbPath, SceneBuilder sceneBuilder)
    {
        try
        {
            // Same call CUE4Parse uses for single-mesh GLB export (Gltf.cs:111).
            ModelRoot model = sceneBuilder.ToGltf2();
            Directory.CreateDirectory(Path.GetDirectoryName(glbPath)!);
            var glb = model.WriteGLB();
            using var stream = File.Create(glbPath);
            stream.Write(glb.Array!, glb.Offset, glb.Count);
            return true;
        }
        catch (Exception ex)
        {
            LogError($"[GlbScene] GLB write failed ({glbPath}): {ex.Message}");
            return false;
        }
    }

    // One buffered punctual light: the glTF KHR_lights_punctual parameters plus
    // the world node matrix, captured during the render pass and emitted by
    // WritePendingLights.
    private readonly struct PendingLight
    {
        public readonly PunctualLightType LightType;
        public readonly Vector3 Color;
        public readonly float Intensity;
        public readonly float Range;
        public readonly float InnerConeRadians;
        public readonly float OuterConeRadians;
        public readonly string Name;
        public readonly Matrix4x4 NodeMatrix;
        public readonly string? ExtrasJson;

        public PendingLight(
            PunctualLightType lightType,
            Vector3 color,
            float intensity,
            float range,
            float innerConeRadians,
            float outerConeRadians,
            string name,
            Matrix4x4 nodeMatrix,
            string? extrasJson)
        {
            LightType = lightType;
            Color = color;
            Intensity = intensity;
            Range = range;
            InnerConeRadians = innerConeRadians;
            OuterConeRadians = outerConeRadians;
            Name = name;
            NodeMatrix = nodeMatrix;
            ExtrasJson = extrasJson;
        }
    }

    // One buffered camera: the resolved glTF projection parameters plus the
    // world node matrix, captured during the render pass and emitted by
    // WritePendingLightsAndCameras.
    private readonly struct PendingCamera
    {
        public readonly bool IsOrthographic;
        public readonly float? AspectRatio;  // perspective only (null = unconstrained)
        public readonly float VerticalFov;   // perspective only, radians
        public readonly float XMag;          // orthographic only
        public readonly float YMag;          // orthographic only
        public readonly float ZNear;
        public readonly float ZFar;
        public readonly string Name;
        public readonly Matrix4x4 NodeMatrix;

        private PendingCamera(bool isOrthographic, float? aspectRatio, float verticalFov, float xMag, float yMag, float zNear, float zFar, string name, Matrix4x4 nodeMatrix)
        {
            IsOrthographic = isOrthographic;
            AspectRatio = aspectRatio;
            VerticalFov = verticalFov;
            XMag = xMag;
            YMag = yMag;
            ZNear = zNear;
            ZFar = zFar;
            Name = name;
            NodeMatrix = nodeMatrix;
        }

        public static PendingCamera CreatePerspective(float? aspectRatio, float verticalFov, float zNear, float zFar, string name, Matrix4x4 nodeMatrix)
            => new(false, aspectRatio, verticalFov, 0f, 0f, zNear, zFar, name, nodeMatrix);

        public static PendingCamera CreateOrthographic(float xMag, float yMag, float zNear, float zFar, string name, Matrix4x4 nodeMatrix)
            => new(true, null, 0f, xMag, yMag, zNear, zFar, name, nodeMatrix);
    }

    // Shared mesh cache key: LightingGuid + ordered override material PathNames.
    // Identical mesh + identical override list = share; any difference = a
    // fresh MeshBuilder. This is the gotcha-list fix for `agent migration
    // index-key inconsistency`: both sides of the cache (write/read) compute
    // the key the same way (the resolver builds the override-name list once
    // per placement and the context uses it both as cache key and as the
    // input to BuildMesh's override loop, so a write-side miss is impossible).
    private readonly struct MeshShareKey : IEquatable<MeshShareKey>
    {
        private readonly CUE4Parse.UE4.Objects.Core.Misc.FGuid _lightingGuid;
        private readonly string _overrideMaterialSignature;

        public MeshShareKey(
            CUE4Parse.UE4.Objects.Core.Misc.FGuid lightingGuid,
            IReadOnlyList<string> overrideMaterialPathNames)
        {
            _lightingGuid = lightingGuid;
            _overrideMaterialSignature = overrideMaterialPathNames.Count == 0
                ? string.Empty
                : string.Join("", overrideMaterialPathNames);
        }

        public bool Equals(MeshShareKey other)
        {
            return _lightingGuid.Equals(other._lightingGuid)
                && string.Equals(_overrideMaterialSignature, other._overrideMaterialSignature, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is MeshShareKey other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(_lightingGuid.GetHashCode(), _overrideMaterialSignature);
        }
    }
}
