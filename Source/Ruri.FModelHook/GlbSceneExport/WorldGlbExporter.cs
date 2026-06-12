using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.glTF;
using CUE4Parse_Conversion.Meshes.PSK;
using FModel.Views.Snooper;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace Ruri.FModelHook.GlbSceneExport;

using MESH = MeshBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>;

// Exports a whole UE map as glTF binary (.glb) scene(s).
//
// Geometry is produced by CUE4Parse's own glTF section exporter
// (Gltf.ExportStaticMeshSections) so every mesh is byte-identical to what
// FModel writes when you "Save Model" a single static mesh as GLB. On top of
// that we build a multi-node SharpGLTF SceneBuilder: each placed component
// becomes one AddRigidMesh(mesh, worldMatrix) call, and identical meshes
// (matched by LightingGuid) share a single MeshBuilder so glTF stores them once
// and instances them — exactly how FModel's preview instances them.
//
// Actor discovery (including World Partition aggregation) is delegated to
// WorldActorCollector; the per-actor mesh resolution below is a 1:1 port of
// FModel Renderer.WorldMesh / ProcessMesh (Renderer.cs:533-690).
//
// SCALE: an open world expands its InstancedStaticMeshComponents into hundreds
// of thousands of placements. SharpGLTF's ToGltf2() materialises one Schema2
// node per placement and runs out of memory well before that. So the scene is
// written in BUDGETED PARTS: once the in-flight SceneBuilder reaches
// MaxInstancesPerGlb nodes it is flushed to "<map>.partNNN.glb" and a fresh
// builder continues. Every part is world-space aligned, so importing them all
// reconstitutes the complete scene. A map that fits in one part is written as
// a single "<map>.glb" with no suffix.
public sealed class WorldGlbExporter
{
    // Node budget per .glb part. Kept well under the point where ToGltf2()
    // exhausts memory on a large open world, while keeping the part count low.
    private const int MaxInstancesPerGlb = 50_000;

    private readonly IFileProvider _provider;
    private readonly ExporterOptions _options;
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    // Reset on every part flush so peak memory is bounded by one part.
    private SceneBuilder _sceneBuilder = new();
    private Dictionary<FGuid, MESH?> _meshCache = new();

    // Persist across parts: materials are written once (deduped); the distinct
    // mesh / part bookkeeping is for the summary log.
    private readonly List<MaterialExporter2> _materialExporters = new();
    private readonly HashSet<string> _writtenMaterialKeys = new(StringComparer.Ordinal);
    private readonly HashSet<FGuid> _distinctMeshGuids = new();
    private readonly List<string> _writtenParts = new();

    private string _outputBasePath = string.Empty;
    private int _placementCount;
    private int _batchInstanceCount;
    private int _overrideMaterialSkips;

    public WorldGlbExporter(IFileProvider provider, ExporterOptions options, Action<string> log, Action<string> logError)
    {
        _provider = provider;
        _options = options;
        _log = log;
        _logError = logError;
    }

    public bool Export(UWorld world, string sourcePackageKey, string outputDirectory, CancellationToken cancellationToken)
    {
        string worldPackagePath = world.Owner?.Name ?? world.GetPathName();
        // The provider Files key (physical/virtual path, e.g.
        // "Oni_Valley_VFX/Content/.../Oni_Valley") is what the World Partition
        // cell + external-actor packages are keyed by; world.Owner.Name is the
        // logical "/Game/..." mount path, which does NOT match Files keys. Use
        // the file key (minus extension) for the path-based cell scans.
        string scanKey = StripExtension(sourcePackageKey);
        _log($"[GlbScene] Exporting world '{worldPackagePath}' (file key '{scanKey}') ...");

        _outputBasePath = BuildOutputBase(outputDirectory, worldPackagePath);

        var collector = new WorldActorCollector(_provider, _log, _logError, cancellationToken);
        List<WorldActor> actors = collector.Collect(world, scanKey);

        _log($"[GlbScene] Building scene in <= {MaxInstancesPerGlb}-instance .glb parts...");
        foreach (var placement in actors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProcessActor(placement.Actor, placement.BaseTransform);
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Actor '{(placement.Actor as UObject)?.Name}' failed: {ex.Message}");
            }
        }
        FlushBatch();

        if (_overrideMaterialSkips > 0)
        {
            _log($"[GlbScene] Note: {_overrideMaterialSkips} component(s) used per-instance OverrideMaterials; " +
                 "the shared instanced mesh keeps its base materials (per-variant material export is a future phase).");
        }

        if (_placementCount == 0 || _writtenParts.Count == 0)
        {
            _logError($"[GlbScene] No renderable meshes written for '{worldPackagePath}'.");
            return false;
        }

        if (_options.ExportMaterials)
        {
            _log($"[GlbScene] Exporting {_materialExporters.Count} materials/textures...");
            WriteMaterials(outputDirectory);
            _log("[GlbScene] Materials/textures exported.");
        }

        string outputDescription = FinalizeParts();
        _log($"[GlbScene] Done. placements={_placementCount} uniqueMeshes={_distinctMeshGuids.Count} " +
             $"parts={_writtenParts.Count} materials={_materialExporters.Count} -> {outputDescription}");
        return true;
    }

    // 1:1 port of FModel Renderer.WorldMesh (Renderer.cs:533-584).
    private void ProcessActor(IPropertyHolder actor, Transform baseTransform)
    {
        if (actor.TryGetValue(out FPackageIndex[] instanceComponents, "InstanceComponents"))
        {
            foreach (var component in instanceComponents)
            {
                if (!component.TryLoad(out UStaticMeshComponent staticMeshComponent) ||
                    !staticMeshComponent.GetStaticMesh().TryLoad(out UStaticMesh mesh) ||
                    mesh.Materials.Length < 1)
                    continue;

                var relation = SceneTransform.CalculateTransform(staticMeshComponent, baseTransform);
                if (staticMeshComponent is UInstancedStaticMeshComponent { PerInstanceSMData.Length: > 0 } instanced)
                {
                    foreach (var perInstance in instanced.PerInstanceSMData!)
                    {
                        AddMeshInstance(mesh, staticMeshComponent, SceneTransform.InstanceTransform(
                            relation,
                            perInstance.TransformData.Translation,
                            perInstance.TransformData.Rotation,
                            perInstance.TransformData.Scale3D));
                    }
                }
                else
                {
                    AddMeshInstance(mesh, staticMeshComponent, relation);
                }
            }
        }
        else if (actor.TryGetValue(out FPackageIndex componentTemplate, "ComponentTemplate") &&
                 componentTemplate.TryLoad(out UObject template))
        {
            if (!template.TryGetValue(out UStaticMesh mesh, "StaticMesh") &&
                template.TryGetValue(out FPackageIndex restCollection, "RestCollection") &&
                restCollection.TryLoad(out UGeometryCollection geometryCollection) &&
                geometryCollection.RootProxyData is { ProxyMeshes.Length: > 0 } rootProxyData)
            {
                rootProxyData.ProxyMeshes[0].TryLoad(out mesh);
            }

            if (mesh is { Materials.Length: > 0 })
            {
                AddMeshInstance(mesh, template, SceneTransform.CalculateTransform(template, baseTransform));
            }
        }
        else if (actor.TryGetValue(out FPackageIndex staticMeshComponentIndex, "StaticMeshComponent", "ComponentTemplate", "StaticMesh", "Mesh", "LightMesh", "SplineMesh") &&
                 staticMeshComponentIndex.TryLoad(out UStaticMeshComponent staticMeshComponent) &&
                 staticMeshComponent.GetStaticMesh().TryLoad(out UStaticMesh mesh) &&
                 mesh.Materials.Length > 0)
        {
            AddMeshInstance(mesh, staticMeshComponent, SceneTransform.CalculateTransform(staticMeshComponent, baseTransform));
        }
    }

    private void AddMeshInstance(UStaticMesh mesh, UObject component, Transform placement)
    {
        if (component.TryGetValue(out FPackageIndex[] overrideMaterials, "OverrideMaterials") && overrideMaterials.Length > 0)
        {
            _overrideMaterialSkips++;
        }

        if (!_meshCache.TryGetValue(mesh.LightingGuid, out var meshBuilder))
        {
            meshBuilder = BuildMesh(mesh);
            _meshCache[mesh.LightingGuid] = meshBuilder;
        }
        if (meshBuilder == null) return;

        _distinctMeshGuids.Add(mesh.LightingGuid);
        _sceneBuilder.AddRigidMesh(meshBuilder, SceneTransform.NodeMatrix(placement));
        _placementCount++;
        _batchInstanceCount++;

        if (_batchInstanceCount >= MaxInstancesPerGlb)
        {
            FlushBatch();
        }
    }

    // Materialise the current SceneBuilder to a .glb part and start a fresh one
    // so peak memory stays bounded by a single part's node count.
    private void FlushBatch()
    {
        if (_batchInstanceCount == 0) return;

        string partPath = $"{_outputBasePath}.part{_writtenParts.Count:D3}.glb";
        if (WriteSceneTo(partPath, _sceneBuilder))
        {
            _writtenParts.Add(partPath);
            _log($"[GlbScene] Wrote part {_writtenParts.Count - 1} ({_batchInstanceCount} instances) -> {partPath}");
        }

        _sceneBuilder = new SceneBuilder();
        _meshCache = new Dictionary<FGuid, MESH?>();
        _batchInstanceCount = 0;
    }

    private MESH? BuildMesh(UStaticMesh mesh)
    {
        if (!mesh.TryConvert(out var convertedMesh, _options.NaniteMeshFormat) || convertedMesh.LODs.Count == 0)
        {
            _logError($"[GlbScene] Mesh '{mesh.Name}' has no LODs; skipped.");
            return null;
        }

        var lod = convertedMesh.LODs[0];
        if (lod.Sections.Value == null)
        {
            _logError($"[GlbScene] Mesh '{mesh.Name}' LOD0 has no sections; skipped.");
            return null;
        }

        var meshBuilder = new MESH(mesh.Name);
        var sections = lod.Sections.Value;
        for (int i = 0; i < sections.Length; i++)
        {
            CMeshSection section = sections[i];
            string? materialKey = section.Material?.Load<UMaterialInterface>()?.GetPathName();

            int before = _options.ExportMaterials ? _materialExporters.Count : 0;
            Gltf.ExportStaticMeshSections(i, lod, section, _options.ExportMaterials ? _materialExporters : null, meshBuilder, _options);

            // De-duplicate the material exporter just appended so a material
            // shared across the scene is decoded and written only once (even
            // though its geometry is re-emitted into each part that uses it).
            if (_options.ExportMaterials && _materialExporters.Count > before)
            {
                if (materialKey == null || !_writtenMaterialKeys.Add(materialKey))
                {
                    _materialExporters.RemoveRange(before, _materialExporters.Count - before);
                }
            }
        }
        return meshBuilder;
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
            _logError($"[GlbScene] GLB write failed ({glbPath}): {ex.Message}");
            return false;
        }
    }

    private void WriteMaterials(string outputDirectory)
    {
        if (!_options.ExportMaterials) return;
        var directory = new DirectoryInfo(outputDirectory);
        foreach (var material in _materialExporters)
        {
            try
            {
                material.TryWriteToDir(directory, out _, out _);
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Material export failed: {ex.Message}");
            }
        }
    }

    // When the whole world fit in a single part, drop the ".partNNN" suffix so
    // small maps produce a clean "<map>.glb". Returns a human-readable summary
    // of what was written.
    private string FinalizeParts()
    {
        if (_writtenParts.Count == 1)
        {
            string single = _outputBasePath + ".glb";
            try
            {
                if (!string.Equals(single, _writtenParts[0], StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(single)) File.Delete(single);
                    File.Move(_writtenParts[0], single);
                    _writtenParts[0] = single;
                }
            }
            catch (Exception ex)
            {
                _logError($"[GlbScene] Could not rename single part to '{single}': {ex.Message}");
                return _writtenParts[0];
            }
            return single;
        }

        return $"{_writtenParts.Count} parts ('{Path.GetFileName(_outputBasePath)}.partNNN.glb')";
    }

    // Map package path under the output root WITHOUT extension; the ".glb" (or
    // ".partNNN.glb") suffix is appended by the writer. Materials/textures land
    // in their own package-path-mirrored folders under the same root.
    private static string BuildOutputBase(string outputDirectory, string worldPackagePath)
    {
        string relative = worldPackagePath.Replace('\\', '/');
        if (relative.StartsWith('/')) relative = relative[1..];
        return Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    // Strips a trailing package extension (".umap"/".uasset") without touching
    // dots inside directory names.
    private static string StripExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash ? path[..dot] : path;
    }
}
