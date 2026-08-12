using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.Gltf;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

using MESH = MeshBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>;

public sealed class GlbSceneContext
{
    public const int MaxInstancesPerGlb = 50_000;

    private const EMeshQuality SceneMeshQuality = EMeshQuality.Highest;

    public IFileProvider Provider { get; }
    public ExportOptions Options { get; }
    public Action<string> Log { get; }
    public Action<string> LogError { get; }
    public GlbMaterialFactory MaterialFactory { get; }
    public SceneManifest Manifest { get; }

    private SceneBuilder _sceneBuilder = new();
    private readonly Dictionary<MeshShareKey, MESH?> _meshCache = new();

    private readonly List<UMaterialInterface> _materials = new();
    private readonly List<string> _materialKeys = new();
    private readonly HashSet<string> _writtenMaterialKeys = new(StringComparer.Ordinal);
    private readonly HashSet<MeshShareKey> _distinctMeshKeys = new();
    private readonly List<string> _writtenParts = new();

    private readonly List<PendingLight> _pendingLights = new();

    private readonly List<PendingCamera> _pendingCameras = new();

    private string _outputBasePath = string.Empty;
    private int _placementCount;
    private int _batchInstanceCount;

    public GlbSceneContext(
        IFileProvider provider,
        ExportOptions options,
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
    public int MaterialCount => _materials.Count;
    public IReadOnlyList<string> WrittenParts => _writtenParts;
    public IReadOnlyList<UMaterialInterface> Materials => _materials;
    public IReadOnlyList<string> MaterialKeys => _materialKeys;
    public string OutputBasePath => _outputBasePath;

    public void SetOutputBasePath(string outputBasePath)
    {
        _outputBasePath = outputBasePath;
    }

    public bool AddRigidMesh(
        UStaticMesh mesh,
        IReadOnlyList<UMaterialInterface?> overrideMaterials,
        IReadOnlyList<string> overrideMaterialPathNames,
        Matrix4x4 nodeMatrix)
    {
        MeshShareKey key = new(mesh.LightingGuid, overrideMaterialPathNames);
        if (!_meshCache.TryGetValue(key, out var meshBuilder))
        {
            meshBuilder = BuildMesh(mesh.Name, () => new StaticMeshDto(mesh, SceneMeshQuality, Options.NaniteMeshFormat), overrideMaterials);
            _meshCache[key] = meshBuilder;
        }
        if (meshBuilder == null)
        {
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

    public bool AddSplineMesh(
        USplineMeshComponent spline,
        string meshName,
        FGuid bendGuid,
        IReadOnlyList<UMaterialInterface?> overrideMaterials,
        IReadOnlyList<string> overrideMaterialPathNames,
        Matrix4x4 nodeMatrix)
    {
        MeshShareKey key = new(bendGuid, overrideMaterialPathNames);
        if (!_meshCache.TryGetValue(key, out var meshBuilder))
        {
            meshBuilder = BuildMesh(meshName, () => new StaticMeshDto(spline, SceneMeshQuality), overrideMaterials);
            _meshCache[key] = meshBuilder;
        }
        if (meshBuilder == null) return false;

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
                    node.Extras = System.Text.Json.Nodes.JsonNode.Parse(light.ExtrasJson);
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

    private MESH? BuildMesh(
        string meshName,
        Func<StaticMeshDto> convert,
        IReadOnlyList<UMaterialInterface?> overrideMaterials)
    {
        StaticMeshDto convertedMesh;
        try
        {
            convertedMesh = convert();
        }
        catch (Exception ex)
        {
            LogError($"[GlbScene] Mesh '{meshName}' failed to convert: {ex.Message}");
            return null;
        }

        if (convertedMesh.LODs.Count == 0)
        {
            LogError($"[GlbScene] Mesh '{meshName}' has no LODs; skipped.");
            return null;
        }

        MeshLodDto<MeshVertex> lod = convertedMesh.LODs[0];
        if (lod.Sections.Length == 0)
        {
            LogError($"[GlbScene] Mesh '{meshName}' LOD0 has no sections; skipped.");
            return null;
        }

        var meshBuilder = new MESH(meshName);
        int meshMaterialSlotCount = convertedMesh.Materials.Length;
        for (int sectionIndex = 0; sectionIndex < lod.Sections.Length; sectionIndex++)
        {
            MeshSectionDto section = lod.Sections[sectionIndex];

            UMaterialInterface? overrideMaterial = ResolveOverrideMaterial(
                section.MaterialIndex,
                meshMaterialSlotCount,
                overrideMaterials);

            MeshMaterialDto? materialSlot = convertedMesh.GetMaterial(section);
            UMaterialInterface? sectionMaterial =
                overrideMaterial ?? materialSlot?.Material?.Load<UMaterialInterface>();

            string materialName =
                sectionMaterial?.Name ?? materialSlot?.SlotName ?? $"material_{sectionIndex}";

            GlbMeshSectionBuilder.AddSection(meshBuilder, lod, section, materialName);

            if (Options.ExportMaterials && sectionMaterial != null)
            {
                string materialKey = sectionMaterial.GetPathName();
                if (_writtenMaterialKeys.Add(materialKey))
                {
                    _materials.Add(sectionMaterial);
                    _materialKeys.Add(materialKey);
                }
            }
        }
        return meshBuilder;
    }

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

    private readonly struct PendingCamera
    {
        public readonly bool IsOrthographic;
        public readonly float? AspectRatio;        public readonly float VerticalFov;        public readonly float XMag;        public readonly float YMag;        public readonly float ZNear;
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
