using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Writers.Gltf;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SkiaSharp;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class LandscapeComponentExporter : IComponentExporter
{
    public bool CanExport(UObject component)
    {
        return component is ALandscapeProxy;
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        if (placed.Component is not ALandscapeProxy proxy)
        {
            return;
        }

        string proxyName = proxy.Name;
        string proxyPathName = proxy.GetPathName();

        var components = LoadLandscapeComponents(proxy, context);
        if (components.Length == 0)
        {
            if (proxy.LandscapeComponents.Length == 0)
            {
                context.Log($"[GlbScene] Landscape '{proxyName}' has no LandscapeComponents — lossless-only proxy, no heightmap geometry to emit.");
            }
            else
            {
                context.Log($"[GlbScene] Landscape '{proxyName}': all {proxy.LandscapeComponents.Length} LandscapeComponent index(es) failed to load (see prior errors); skipping heightmap geometry.");
            }
            ForwardNaniteComponents(proxy, placed, context);
            return;
        }

        Transform proxyRootWorldTransform = ResolveProxyRootWorldTransform(proxy, placed.WorldTransform, context);

        LandscapeMeshDto convertedMesh;
        var heightMaps = new Dictionary<string, ImageSharpImage>();
        var weightMaps = new Dictionary<string, SKBitmap>();
        try
        {
            convertedMesh = new LandscapeMeshDto(proxy, ELandscapeFlags.All, components);

            if (convertedMesh.HeightmapTexture is { } heightmap)
            {
                heightMaps.Add("Heightmap", heightmap);
            }
            if (convertedMesh.BitmapTextures is { } bitmaps)
            {
                foreach (var pair in bitmaps)
                {
                    weightMaps.Add(pair.Key, pair.Value);
                }
            }

            if (convertedMesh.LODs.Count == 0)
            {
                context.LogError($"[GlbScene] Landscape '{proxyName}': conversion produced no mesh; geometry skipped.");
                context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' conversion produced no mesh.");
                return;
            }
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': conversion threw: {ex.Message}");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' conversion threw: {ex.Message}");
            return;
        }

        MeshLodDto<MeshVertex> lod0 = convertedMesh.LODs[0];
        MeshSectionDto[] sections = lod0.Sections;
        if (sections.Length == 0)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': LOD0 has no sections; geometry skipped.");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' LOD0 has no sections.");
            return;
        }

        string landscapeRoot = Path.Combine(context.OutputBasePath + "_Assets", "Landscape", proxyName);
        Directory.CreateDirectory(landscapeRoot);
        string glbFilePath = Path.Combine(landscapeRoot, proxyName + ".glb");

        var landscapeMeshBuilder = new MeshBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>(proxyName);
        var landscapeMaterials = context.Options.ExportMaterials ? new List<UMaterialInterface>() : null;
        var landscapeMaterialKeys = new HashSet<string>(StringComparer.Ordinal);

        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            MeshSectionDto section = sections[sectionIndex];
            MeshMaterialDto? materialSlot = convertedMesh.GetMaterial(section);
            UMaterialInterface? sectionMaterial = materialSlot?.Material?.Load<UMaterialInterface>();

            string materialName =
                sectionMaterial?.Name ?? materialSlot?.SlotName ?? $"material_{sectionIndex}";

            GlbMeshSectionBuilder.AddSection(landscapeMeshBuilder, lod0, section, materialName);

            if (landscapeMaterials != null && sectionMaterial != null
                && landscapeMaterialKeys.Add(sectionMaterial.GetPathName()))
            {
                landscapeMaterials.Add(sectionMaterial);
            }
        }

        Matrix4x4 nodeMatrix = SceneTransform.NodeMatrix(proxyRootWorldTransform);

        try
        {
            var sceneBuilder = new SceneBuilder();
            sceneBuilder.AddRigidMesh(landscapeMeshBuilder, nodeMatrix);
            ModelRoot model = sceneBuilder.ToGltf2();
            var glbSegment = model.WriteGLB();
            using (var stream = File.Create(glbFilePath))
            {
                stream.Write(glbSegment.Array!, glbSegment.Offset, glbSegment.Count);
            }
            context.Log($"[GlbScene] Landscape '{proxyName}': wrote GLB -> {glbFilePath} (components={components.Length}, sections={sections.Length}).");
            context.Manifest.Render.PartFiles.Add(glbFilePath);
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': GLB write failed: {ex.Message}");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' GLB write failed: {ex.Message}");
        }

        WriteHeightmapSidecars(heightMaps, landscapeRoot, proxyName, proxyPathName, context);

        WriteWeightmapSidecars(weightMaps, landscapeRoot, proxyName, proxyPathName, context);

        WriteLandscapeGuidRecord(proxy, landscapeRoot, proxyName, context);

        WriteProxyTransformRecord(proxy, proxyRootWorldTransform, landscapeRoot, proxyName, context);

        WriteLandscapeMaterials(proxy, landscapeMaterials, landscapeRoot, proxyName, proxyPathName, context);


        ForwardNaniteComponents(proxy, placed, context);
    }

    private static ULandscapeComponent[] LoadLandscapeComponents(ALandscapeProxy proxy, GlbSceneContext context)
    {
        var landscapeComponentIndices = proxy.LandscapeComponents;
        if (landscapeComponentIndices.Length == 0) return Array.Empty<ULandscapeComponent>();

        var loaded = new List<ULandscapeComponent>(landscapeComponentIndices.Length);
        for (int i = 0; i < landscapeComponentIndices.Length; i++)
        {
            var index = landscapeComponentIndices[i];
            if (index == null || index.IsNull) continue;
            try
            {
                var component = index.Load<ULandscapeComponent>();
                if (component != null) loaded.Add(component);
                else
                {
                    string entry = $"{proxy.GetPathName()}.LandscapeComponents[{i}] failed to load as ULandscapeComponent.";
                    context.LogError($"[GlbScene] Landscape: {entry}");
                    context.Manifest.RecordDroppedComponent(entry);
                }
            }
            catch (Exception ex)
            {
                string entry = $"{proxy.GetPathName()}.LandscapeComponents[{i}] threw: {ex.Message}";
                context.LogError($"[GlbScene] Landscape: {entry}");
                context.Manifest.RecordDroppedComponent(entry);
            }
        }
        return loaded.ToArray();
    }

    private static Transform ResolveProxyRootWorldTransform(ALandscapeProxy proxy, Transform baseTransform, GlbSceneContext context)
    {
        if (!proxy.TryGetValue(out FPackageIndex rootComponentIndex, "RootComponent"))
        {
            context.LogError($"[GlbScene] Landscape '{proxy.Name}': proxy has no RootComponent property; landscape geometry will land at the actor's base transform — verify the cook is well-formed.");
            return baseTransform;
        }
        if (rootComponentIndex == null || rootComponentIndex.IsNull)
        {
            context.LogError($"[GlbScene] Landscape '{proxy.Name}': RootComponent FPackageIndex is null/empty; landscape geometry will land at the actor's base transform.");
            return baseTransform;
        }

        UObject? rootComponent;
        try
        {
            rootComponent = rootComponentIndex.Load() as UObject;
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxy.Name}': RootComponent load threw '{ex.Message}'; falling back to actor base transform.");
            return baseTransform;
        }
        if (rootComponent == null)
        {
            context.LogError($"[GlbScene] Landscape '{proxy.Name}': RootComponent load returned null; falling back to actor base transform.");
            return baseTransform;
        }

        return SceneTransform.CalculateTransform(rootComponent, baseTransform);
    }

    private static void WriteHeightmapSidecars(
        Dictionary<string, ImageSharpImage> heightMaps,
        string landscapeRoot,
        string proxyName,
        string proxyPathName,
        GlbSceneContext context)
    {
        foreach (var entry in heightMaps)
        {
            string heightmapFilePath = Path.Combine(landscapeRoot, entry.Key + ".png");
            try
            {
                using var stream = File.Create(heightmapFilePath);
                entry.Value.Save(stream, new PngEncoder());
                context.Manifest.RecordAsset($"Landscape/{proxyName}/{entry.Key}.png");
            }
            catch (Exception ex)
            {
                context.LogError($"[GlbScene] Landscape '{proxyName}': heightmap '{entry.Key}' write failed: {ex.Message}");
                context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' heightmap '{entry.Key}' write failed: {ex.Message}");
            }
        }
    }

    private static void WriteWeightmapSidecars(
        Dictionary<string, SKBitmap> weightMaps,
        string landscapeRoot,
        string proxyName,
        string proxyPathName,
        GlbSceneContext context)
    {
        foreach (var entry in weightMaps)
        {
            string weightmapFilePath = Path.Combine(landscapeRoot, entry.Key + ".png");
            try
            {
                using var encoded = entry.Value.Encode(SKEncodedImageFormat.Png, 100);
                if (encoded == null)
                {
                    context.LogError($"[GlbScene] Landscape '{proxyName}': weightmap '{entry.Key}' encode returned null.");
                    context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' weightmap '{entry.Key}' encode returned null.");
                    continue;
                }
                File.WriteAllBytes(weightmapFilePath, encoded.ToArray());
                context.Manifest.RecordAsset($"Landscape/{proxyName}/{entry.Key}.png");
            }
            catch (Exception ex)
            {
                context.LogError($"[GlbScene] Landscape '{proxyName}': weightmap '{entry.Key}' write failed: {ex.Message}");
                context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' weightmap '{entry.Key}' write failed: {ex.Message}");
            }
        }
    }

    private static void WriteLandscapeGuidRecord(ALandscapeProxy proxy, string landscapeRoot, string proxyName, GlbSceneContext context)
    {
        string guidFilePath = Path.Combine(landscapeRoot, "Guid_" + proxy.LandscapeGuid);
        try
        {
            File.WriteAllText(guidFilePath, proxy.LandscapeGuid.ToString());
            context.Manifest.RecordAsset($"Landscape/{proxyName}/Guid_{proxy.LandscapeGuid}");
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': GUID record write failed: {ex.Message}");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxy.GetPathName()}' GUID record write failed: {ex.Message}");
        }
    }

    private static void WriteProxyTransformRecord(
        ALandscapeProxy proxy,
        Transform proxyRootWorldTransform,
        string landscapeRoot,
        string proxyName,
        GlbSceneContext context)
    {
        string transformFilePath = Path.Combine(landscapeRoot, proxyName + ".transform.json");
        var transformRecord = new
        {
            ProxyPackagePath = proxy.GetPathName(),
            ProxyExportType = proxy.ExportType,
            ProxyName = proxyName,
            ComponentSizeQuads = proxy.ComponentSizeQuads,
            SubsectionSizeQuads = proxy.SubsectionSizeQuads,
            NumSubsections = proxy.NumSubsections,
            LandscapeSectionOffset = proxy.LandscapeSectionOffset,
            PositionMeters = new
            {
                X = proxyRootWorldTransform.Position.X,
                Y = proxyRootWorldTransform.Position.Y,
                Z = proxyRootWorldTransform.Position.Z,
            },
            Rotation = new
            {
                X = proxyRootWorldTransform.Rotation.X,
                Y = proxyRootWorldTransform.Rotation.Y,
                Z = proxyRootWorldTransform.Rotation.Z,
                W = proxyRootWorldTransform.Rotation.W,
            },
            Scale = new
            {
                X = proxyRootWorldTransform.Scale.X,
                Y = proxyRootWorldTransform.Scale.Y,
                Z = proxyRootWorldTransform.Scale.Z,
            },
        };
        try
        {
            File.WriteAllText(transformFilePath, JsonConvert.SerializeObject(transformRecord, Formatting.Indented));
            context.Manifest.RecordAsset($"Landscape/{proxyName}/{proxyName}.transform.json");
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': transform record write failed: {ex.Message}");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxy.GetPathName()}' transform record write failed: {ex.Message}");
        }
    }

    private static void WriteLandscapeMaterials(
        ALandscapeProxy proxy,
        List<UMaterialInterface>? landscapeMaterials,
        string landscapeRoot,
        string proxyName,
        string proxyPathName,
        GlbSceneContext context)
    {
        try
        {
            if (!proxy.LandscapeMaterial.IsNull)
            {
                var landscapeMaterial = proxy.LandscapeMaterial.Load<UMaterialInterface>();
                if (landscapeMaterial != null)
                {
                    context.MaterialFactory.RegisterUnique(landscapeMaterial);
                }
            }
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': LandscapeMaterial register failed: {ex.Message}");
        }

        if (landscapeMaterials == null || landscapeMaterials.Count == 0) return;

        var materialFactory = new GlbMaterialFactory(context.Log, context.LogError);
        foreach (UMaterialInterface landscapeSectionMaterial in landscapeMaterials)
        {
            try
            {
                materialFactory.RegisterMaterial(landscapeSectionMaterial, context.Options);
            }
            catch (Exception ex)
            {
                context.LogError($"[GlbScene] Landscape '{proxyName}': material register threw: {ex.Message}");
                context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' material register threw: {ex.Message}");
            }
        }

        try
        {
            materialFactory.WriteSidecars(landscapeRoot);
            context.Manifest.RecordAsset($"Landscape/{proxyName}/");
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': material write threw: {ex.Message}");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' material write threw: {ex.Message}");
        }
    }

    private void ForwardNaniteComponents(ALandscapeProxy proxy, PlacedComponent placed, GlbSceneContext context)
    {
        if (proxy.NaniteComponents == null || proxy.NaniteComponents.Length == 0) return;

        var staticExporter = new StaticMeshComponentExporter();

        foreach (var naniteIndex in proxy.NaniteComponents)
        {
            if (naniteIndex == null || naniteIndex.IsNull) continue;

            UObject? naniteComponent;
            try
            {
                naniteComponent = naniteIndex.Load() as UObject;
            }
            catch (Exception ex)
            {
                context.LogError($"[GlbScene] Landscape '{proxy.Name}': NaniteComponent load threw: {ex.Message}");
                context.Manifest.RecordDroppedComponent($"Landscape proxy '{proxy.GetPathName()}' NaniteComponent load threw: {ex.Message}");
                continue;
            }
            if (naniteComponent is not UStaticMeshComponent) continue;

            Transform componentWorldTransform = SceneTransform.CalculateTransform(naniteComponent, placed.WorldTransform);
            var naniteLeaf = new PlacedComponent(naniteComponent, componentWorldTransform, placed.OwnerActor);

            if (staticExporter.CanExport(naniteComponent))
            {
                staticExporter.Export(in naniteLeaf, context);
            }
            else
            {
                context.Manifest.RecordDroppedComponent($"Landscape proxy '{proxy.GetPathName()}' NaniteComponent '{(naniteComponent as UObject)?.Name}' rejected by static exporter.");
            }
        }
    }
}
