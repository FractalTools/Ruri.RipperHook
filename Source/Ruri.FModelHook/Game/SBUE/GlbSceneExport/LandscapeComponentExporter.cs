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
// Disambiguate: both SharpGLTF.Schema2 and SixLabors.ImageSharp export `Image`.
// The landscape heightmap arrives as a SixLabors.ImageSharp.Image.
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Full-fidelity landscape exporter. Builds the proxy's LandscapeMeshDto so the
// per-component vertex/normal/tangent/UV/vertex-color streams are produced
// byte-for-byte identically to FModel's "Save Landscape" output, then folds the
// resulting LOD into a SharpGLTF MeshBuilder through GlbMeshSectionBuilder —
// the same per-section path GlbSceneContext.BuildMesh uses.
//
// Why this exporter writes a SEPARATE .glb (not into GlbSceneContext's shared
// SceneBuilder): the landscape mesh is synthesised from heightmap+weightmap
// textures plus per-component layer allocations, NOT from a UStaticMesh, so
// there is no LightingGuid for the context's mesh-share key. The proxy emits
// its own .glb under <outputBasePath>_Assets/Landscape/<proxyName>.glb and
// the orchestrator's manifest carries the file in Render.PartFiles. The
// rendered output stays whole (consumer loads main parts + landscape parts).
//
// Coordinate handling:
//   * The converter writes each landscape vertex as
//     `vertCoord + comp.GetRelativeLocation()` — that is, the resulting mesh
//     is in PROXY-LOCAL space. The proxy's own
//     RootComponent transform (RelativeLocation / RelativeRotation /
//     RelativeScale3D — by default `(128,128,256)`, Landscape.cpp:1632) is
//     not applied. We must therefore place the GLB's root node at the proxy's
//     world transform so the rendered mesh lands at the right world position
//     and scale. The transform is folded through SceneTransform.CalculateTransform
//     against `placed.WorldTransform` (the resolver's base) so attach-parent
//     chains are honoured for any future composition with level instances.
//   * Geometry conversion (cm -> m + Z-up -> Y-up) lives inside
//     GlbMeshSectionBuilder, which the static-mesh pipeline also
//     relies on. The node matrix is built with SceneTransform.NodeMatrix so
//     the placement convention (N = W, FModel's Transform.Matrix) is
//     identical to the static-mesh path. No parallel coordinate convention
//     exists.
//
// Heightmaps + weightmaps:
//   * Written as PNG sidecars under
//     <outputBasePath>_Assets/Landscape/<proxyName>/. Heightmap is L16-encoded
//     by the converter (one channel, 16-bit unsigned height) and PngEncoder is
//     the SixLabors writer CUE4Parse's landscape export uses.
//     Weightmaps are SKBitmap (Gray8) — written the same way.
//   * The DirectX-normal sidecar ("NormalMap_DX.png") is always produced by
//     the converter and always carried through (every byte preserved).
//   * Landscape GUID file (`Guid_<guid>`) is emitted alongside so a downstream
//     re-link still has the original FGuid available — same record shape
//     CUE4Parse's landscape export writes.
//
// Materials:
//   * proxy.LandscapeMaterial is loaded and registered with
//     GlbSceneContext.MaterialFactory so it appears in the unique-material
//     audit. Each section's own material is collected and written through
//     GlbMaterialFactory under the same Assets/Landscape/<proxyName>/ path.
//     ExportMaterials=false suppresses the decode pass.
//
// Nanite landscape components (UE5.3+):
//   * proxy.NaniteComponents[] is ULandscapeNaniteComponent which derives from
//     UStaticMeshComponent (LandscapeNaniteComponent.h:82). They carry a baked
//     UStaticMesh proxy of the landscape geometry and are forwarded to the
//     SHARED context via the static-mesh exporter so they participate in the
//     ISM cache / OverrideMaterials path. This means a Nanite landscape map
//     gets BOTH the heightmap-derived geometry (this exporter) AND the baked
//     static proxy (delegated) — that is faithful to the cooked package: both
//     representations exist in the .uasset and the user requirement is "every
//     byte" so we do not deduplicate. The lossless layer captures the
//     property tree of both; consumers can pick whichever proxy fits their
//     pipeline.
//
// Manifest accounting:
//   * The proxy .glb path is appended to Manifest.Render.PartFiles and the
//     part-file counter is incremented (so the orchestrator's final summary is
//     truthful even though this exporter bypasses GlbSceneContext.FlushBatch).
//   * Heightmap / weightmap / GUID sidecars + the material JSON / textures are
//     each recorded as closure-layer assets via Manifest.RecordAsset.
//   * Per-component lossless tallying (LosslessLayer.ComponentCount) is OWNED
//     by CompleteSceneDataExporter — it walks the actor's export-reference
//     graph (BlueprintCreatedComponents, InstanceComponents, plus the
//     LandscapeComponents property array) and increments RecordComponent for
//     each. Calling it from here would double-count, so this exporter does
//     not touch LosslessLayer counters.
public sealed class LandscapeComponentExporter : IComponentExporter
{
    public bool CanExport(UObject component)
    {
        // ALandscape AND ALandscapeStreamingProxy both derive from
        // ALandscapeProxy (ALandscape.cs:42-43), so a single type test claims
        // every landscape actor the resolver yields.
        return component is ALandscapeProxy;
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        if (placed.Component is not ALandscapeProxy proxy)
        {
            // Defensive: CanExport already filtered to ALandscapeProxy.
            return;
        }

        string proxyName = proxy.Name;
        string proxyPathName = proxy.GetPathName();

        // Resolve LandscapeComponents (ULandscapeComponent[]). The TryConvert
        // overload accepts a null components array and will load them itself,
        // but we do the load here so a single component-load failure is logged
        // with the proxy context (and so we can drive Manifest.RecordComponent
        // truthfully).
        var components = LoadLandscapeComponents(proxy, context);
        if (components.Length == 0)
        {
            // Two routes land here: (a) the proxy genuinely carries no
            // landscape components (a benign "lossless-only" proxy — common
            // for editor-only LandscapeStreamingProxy stubs that exist only
            // to anchor naming), (b) every component index failed to load
            // (the per-index drop reasons were already pushed in
            // LoadLandscapeComponents, so the count is honest). Either way
            // there is no heightmap-derived geometry to emit; we log without
            // an ASSET-level drop so the manifest's dropped count is not
            // inflated for the benign case.
            //
            // IMPORTANT (byte-completeness): a UE5.3+ fully-Nanite landscape
            // can carry the baked static proxy in `NaniteComponents[]` even
            // when `LandscapeComponents[]` is empty in the cook (the editor
            // may strip the source ULandscapeComponents once the Nanite
            // proxy is built). We MUST still forward those Nanite components
            // through the static-mesh path so the bytes land in the scene —
            // otherwise the early-return drops every triangle of a Nanite-
            // only landscape proxy. The static-mesh exporter is invoked
            // directly (no proxy heightmap-derived geometry needed).
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

        // Compute the proxy's world transform (RootComponent walked through the
        // AttachParent chain). For an embedded proxy with RootComponent at
        // identity this is identity; for a proxy whose RootComponent has the
        // default landscape scale (128,128,256) — Landscape.cpp:1632 — this
        // applies the scale + position so the GLB lands at the cooked world
        // location. SceneTransform.CalculateTransform reads RelativeLocation /
        // RelativeRotation / RelativeScale3D and walks AttachParent exactly
        // like FModel Renderer.CalculateTransform.
        Transform proxyRootWorldTransform = ResolveProxyRootWorldTransform(proxy, placed.WorldTransform, context);

        // Build the DTO once. ELandscapeFlags.All produces the mesh, the L16
        // heightmap, AND the SKBitmap weightmap dictionary (+ the DirectX
        // normal map) in a single parallel pass — every byte the cooked
        // landscape carries is materialised here.
        LandscapeMeshDto convertedMesh;
        var heightMaps = new Dictionary<string, ImageSharpImage>();
        var weightMaps = new Dictionary<string, SKBitmap>();
        try
        {
            // Same constructor LandscapeMeshExporter drives internally, so the
            // geometry stays byte-identical to the stock landscape export.
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

        // Build the destination directory layout under the closure-layer namespace.
        string landscapeRoot = Path.Combine(context.OutputBasePath + "_Assets", "Landscape", proxyName);
        Directory.CreateDirectory(landscapeRoot);
        string glbFilePath = Path.Combine(landscapeRoot, proxyName + ".glb");

        // Build the SharpGLTF MeshBuilder through the SAME per-section path as
        // the regular static-mesh pipeline (GlbSceneContext.BuildMesh), so the
        // vertex/index triangles land in glTF-local space (SwapYZ + *0.01)
        // byte-for-byte the same way.
        var landscapeMeshBuilder = new MeshBuilder<VertexPositionNormalTangent, VertexColorXTextureX, VertexEmpty>(proxyName);
        var landscapeMaterials = context.Options.ExportMaterials ? new List<UMaterialInterface>() : null;
        var landscapeMaterialKeys = new HashSet<string>(StringComparer.Ordinal);

        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            MeshSectionDto section = sections[sectionIndex];
            MeshMaterialDto? materialSlot = convertedMesh.GetMaterial(section);
            UMaterialInterface? sectionMaterial = materialSlot?.Material?.Load<UMaterialInterface>();

            // Same naming rule as GlbSceneContext.BuildMesh: the embed pass
            // looks bundles up by UMaterialInterface.Name.
            string materialName =
                sectionMaterial?.Name ?? materialSlot?.SlotName ?? $"material_{sectionIndex}";

            GlbMeshSectionBuilder.AddSection(landscapeMeshBuilder, lod0, section, materialName);

            if (landscapeMaterials != null && sectionMaterial != null
                && landscapeMaterialKeys.Add(sectionMaterial.GetPathName()))
            {
                landscapeMaterials.Add(sectionMaterial);
            }
        }

        // Compose the node matrix (N = W, FModel's placement matrix) via
        // SceneTransform.NodeMatrix exactly like the static-mesh path. This
        // is THE bridge that pairs the byte-identical mesh export with the
        // byte-identical proxy placement.
        Matrix4x4 nodeMatrix = SceneTransform.NodeMatrix(proxyRootWorldTransform);

        // Write the .glb. We construct a one-mesh SceneBuilder so the file is
        // self-contained — same shape as Gltf.cs:38 but with a non-identity
        // root transform so the cooked world position is encoded.
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
            // Append to PartFiles so the manifest's render-layer file list is
            // complete. We do NOT touch PartFileCount because WorldGlbExporter.
            // FinalizePartsAndRecordManifest sets it to writtenParts.Count
            // (the main-scene part count only) after the render pass — so
            // PartFiles ends up exhaustive (main parts + landscape parts) and
            // PartFiles.Count is the trustworthy total. The dedicated counter
            // is owned by the orchestrator.
            context.Manifest.Render.PartFiles.Add(glbFilePath);
        }
        catch (Exception ex)
        {
            context.LogError($"[GlbScene] Landscape '{proxyName}': GLB write failed: {ex.Message}");
            context.Manifest.RecordDroppedAsset($"Landscape proxy '{proxyPathName}' GLB write failed: {ex.Message}");
        }

        // Heightmap sidecars (PNG, L16) — every entry produced by TryConvert.
        WriteHeightmapSidecars(heightMaps, landscapeRoot, proxyName, proxyPathName, context);

        // Weightmap sidecars (PNG, Gray8 + NormalMap_DX in BGRA).
        WriteWeightmapSidecars(weightMaps, landscapeRoot, proxyName, proxyPathName, context);

        // Landscape GUID record — same shape as LandscapeExporter.cs:108. The
        // file has no extension and contains the GUID's string form so a
        // downstream re-link can recover the original FGuid.
        WriteLandscapeGuidRecord(proxy, landscapeRoot, proxyName, context);

        // Save the per-proxy world transform so a consumer that recomposes
        // multiple landscape streaming proxies into one scene has the
        // truthful proxy placement without parsing the GLB extras.
        WriteProxyTransformRecord(proxy, proxyRootWorldTransform, landscapeRoot, proxyName, context);

        // Materials — register with the factory so the unique count is
        // truthful; write the per-material JSON/textures into the landscape
        // sidecar folder so downstream tools find them next to the GLB.
        WriteLandscapeMaterials(proxy, landscapeMaterials, landscapeRoot, proxyName, proxyPathName, context);

        // NB: per-component manifest tallying for the LOSSLESS layer
        // (LosslessLayer.ComponentCount, LosslessLayer.ComponentsByExportType)
        // is owned by CompleteSceneDataExporter.ExportSingleActor — it walks
        // every export-indexed reference reachable from the actor (including
        // the LandscapeComponents array) and calls Manifest.RecordComponent
        // for each (CompleteSceneDataExporter.cs:199-201). Calling it from
        // here as well would double-count, so the render layer does not
        // touch the manifest's ComponentCount.

        // Forward Nanite landscape components to the static-mesh path so a
        // UE5.3+ map that bakes a Nanite proxy for the landscape ALSO exports
        // the baked static representation. ULandscapeNaniteComponent derives
        // from UStaticMeshComponent (LandscapeNaniteComponent.h:82), so it
        // satisfies the static exporter's CanExport. We thread them through
        // the SHARED context (instance budget aware) instead of writing them
        // here — the bytes go where every other static placement goes.
        ForwardNaniteComponents(proxy, placed, context);
    }

    // Resolve ULandscapeComponent leaves from proxy.LandscapeComponents. Any
    // index that fails to load is logged + counted as a dropped asset; the
    // remainder still goes through TryConvert. We do NOT swallow the proxy
    // wholesale — a partial landscape is still data the consumer wants.
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

    // Walk proxy.RootComponent through the AttachParent chain so the resulting
    // Transform is the proxy's world placement. If RootComponent is absent,
    // fall back to placed.WorldTransform — but EMIT A WARNING because the
    // mesh data carries proxy-LOCAL vertex positions (MeshConverter.cs:657
    // bakes `vertCoord + relLoc` where relLoc is the per-component RelativeLocation
    // in PROXY-LOCAL space, NOT world space). A fall-through to Identity for a
    // proxy whose cooked world transform actually mattered would land the
    // landscape geometry at the WRONG world position. In practice every cooked
    // ALandscapeStreamingProxy carries a RootComponent — this branch fires only
    // on malformed cooks, and the user must see the audit signal.
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

        // SceneTransform.CalculateTransform mirrors the Renderer
        // CalculateTransform (Renderer.cs:676-690). It folds RelativeLocation,
        // RelativeRotation, RelativeScale3D, and the AttachParent chain into a
        // single Transform whose Matrix is the proxy's world matrix.
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
        // Matches LandscapeExporter.cs:108. Filename has no extension; payload
        // is the GUID's string form encoded as UTF-8.
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
            // The Position field on Transform is already pre-scaled by
            // SCALE_DOWN_RATIO (cm -> m) by SceneTransform.CalculateTransform,
            // so the JSON carries glTF-space metres (matching the GLB).
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
        // Always register the proxy's LandscapeMaterial with the factory so the
        // unique-material audit reflects it even when ExportMaterials is off.
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

        // Same JSON + decoded-texture payload the static-mesh sidecar pass
        // writes (MaterialTextureWriter), rooted at the proxy's own folder.
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

    // Forward proxy.NaniteComponents through the static-mesh path. The Nanite
    // landscape proxy is a UStaticMeshComponent subclass (LandscapeNaniteComponent.h:82),
    // so we synthesize a PlacedComponent that the static exporter handles. The
    // owner actor stays as the landscape proxy so the lossless layer
    // attribution still says "this geometry came from <proxyName>".
    private void ForwardNaniteComponents(ALandscapeProxy proxy, PlacedComponent placed, GlbSceneContext context)
    {
        if (proxy.NaniteComponents == null || proxy.NaniteComponents.Length == 0) return;

        // Single instance shared because the static-mesh exporter has no
        // per-call state; matches how the registry constructs it in
        // WorldGlbExporter (one instance for the whole run).
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

            // Walk the Nanite component's own AttachParent chain over the
            // proxy's base transform — matches the resolver's per-leaf
            // handling for any other static-mesh-shaped component.
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
