using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Full-fidelity port of FModel Renderer.ProcessMesh (Renderer.cs:586-674) for
// static-mesh-shaped placements. Three shapes are recognised, in declining
// priority so the legacy single-property fallback never claims an ISM:
//
//   (1) UInstancedStaticMeshComponent (and its HISM/HLOD/Grass subclasses)
//       — fan out into PerInstanceSMData[] and call AddRigidMesh once per
//       instance with SceneTransform.InstanceTransform composed with the
//       component's AttachParent chain (Renderer.cs:547-555).
//   (2) Plain UStaticMeshComponent — straight AddRigidMesh against the
//       cached AttachParent-folded transform.
//   (3) ComponentTemplate (BP CDO StaticMeshComponent or GeometryCollection
//       proxy mesh) — Renderer.cs:561-577 falls into this branch when
//       neither InstanceComponents nor a single StaticMeshComponent slot is
//       present. This file restores that path by handling it INSIDE the
//       static-mesh exporter so it never silently disappears when the actor
//       lookup happens to surface the template instead of a real component.
//
// USplineMeshComponent is INTENTIONALLY rejected by CanExport: it derives
// from UStaticMeshComponent and would otherwise get static-meshified, losing
// the spline-deformed mesh slice geometry. The dispatch table lists the
// spline exporter BEFORE this one so spline placements never reach here.
//
// Material handling (the previously logged "271 components skipped"
// regression): OverrideMaterials is now resolved into a parallel list of
// loaded UMaterialInterface? + their PathNames. The list is fed into
// GlbSceneContext.AddRigidMesh which (a) uses the PathName signature as part
// of the mesh-share cache key — so an overridden placement does not collide
// with a base placement — and (b) substitutes the override material into
// each `CMeshSection.Material` before handing it to
// Gltf.ExportStaticMeshSections so the SharpGLTF primitive sees the
// override material's name. Identical mesh + identical override list still
// instances; any different override list emits a fresh MeshBuilder.
public sealed class StaticMeshComponentExporter : IComponentExporter
{
    public bool CanExport(UObject component)
    {
        if (component is USplineMeshComponent) return false;
        if (component is UStaticMeshComponent) return true;
        // ComponentTemplate path: the resolver may yield the template UObject
        // itself when the actor only exposes a `ComponentTemplate` property.
        // The template is treated as a static-mesh-bearing record if it has
        // a StaticMesh field or a RestCollection-backed proxy mesh.
        return CarriesStaticMeshSlot(component);
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        UObject component = placed.Component;
        Transform worldTransform = placed.WorldTransform;
        IPropertyHolder ownerActor = placed.OwnerActor;

        // (1) ISM + (2) plain SMC ---------------------------------------------
        if (component is UStaticMeshComponent staticMeshComponent)
        {
            if (!staticMeshComponent.GetStaticMesh().TryLoad(out UStaticMesh mesh) ||
                mesh.Materials.Length < 1)
            {
                return;
            }

            BuildOverrideMaterialLists(
                staticMeshComponent,
                out var overrideMaterials,
                out var overrideMaterialPathNames);

            if (staticMeshComponent is UInstancedStaticMeshComponent { PerInstanceSMData.Length: > 0 } instanced)
            {
                foreach (var perInstance in instanced.PerInstanceSMData!)
                {
                    Transform instanceTransform = SceneTransform.InstanceTransform(
                        worldTransform,
                        perInstance.TransformData.Translation,
                        perInstance.TransformData.Rotation,
                        perInstance.TransformData.Scale3D);

                    context.AddRigidMesh(
                        mesh,
                        overrideMaterials,
                        overrideMaterialPathNames,
                        SceneTransform.NodeMatrix(instanceTransform));
                }
            }
            else
            {
                context.AddRigidMesh(
                    mesh,
                    overrideMaterials,
                    overrideMaterialPathNames,
                    SceneTransform.NodeMatrix(worldTransform));
            }
            return;
        }

        // (3) ComponentTemplate -----------------------------------------------
        // Mirrors Renderer.cs:561-577: read the template's StaticMesh (or
        // RestCollection->ProxyMeshes[0]), then add a single placement at
        // CalculateTransform(template, baseTransform). The world transform
        // is already folded by the resolver so we use it directly.
        if (TryResolveTemplateMesh(component, out UStaticMesh? templateMesh) && templateMesh != null)
        {
            BuildOverrideMaterialLists(
                component,
                out var overrideMaterials,
                out var overrideMaterialPathNames);

            context.AddRigidMesh(
                templateMesh,
                overrideMaterials,
                overrideMaterialPathNames,
                SceneTransform.NodeMatrix(worldTransform));
        }

        // ownerActor is reserved for the byte-for-byte bMirrored + TextureData
        // paths (Renderer.cs:604/606); those swap material parameters per
        // placement, which forces a per-placement MeshBuilder. The
        // foundation revision opts to KEEP the placement on the shared mesh
        // and surface the bMirrored / TextureData record into the lossless
        // layer instead, so downstream tools can patch glTF material params
        // in post. The follow-up material cell will fold the swap into the
        // mesh-share key alongside OverrideMaterials.
        _ = ownerActor;
    }

    // Loads the OverrideMaterials array into (a) UMaterialInterface? slots so
    // GlbSceneContext.BuildMesh can substitute per-section materials, and
    // (b) a parallel string list of PathNames the cache key uses to keep the
    // (mesh, overrides) tuple unique. Building both at the same site is the
    // gotcha-list fix for "write-side / read-side key inconsistency": there
    // is exactly ONE place that derives the cache signature from the override
    // material list, so a divergence is impossible by construction.
    private static void BuildOverrideMaterialLists(
        UObject component,
        out IReadOnlyList<UMaterialInterface?> overrideMaterials,
        out IReadOnlyList<string> overrideMaterialPathNames)
    {
        if (!component.TryGetValue(out FPackageIndex[] overrideMaterialIndices, "OverrideMaterials") ||
            overrideMaterialIndices.Length == 0)
        {
            overrideMaterials = System.Array.Empty<UMaterialInterface?>();
            overrideMaterialPathNames = System.Array.Empty<string>();
            return;
        }

        var loadedMaterials = new UMaterialInterface?[overrideMaterialIndices.Length];
        var pathNames = new string[overrideMaterialIndices.Length];
        for (int i = 0; i < overrideMaterialIndices.Length; i++)
        {
            var overrideIndex = overrideMaterialIndices[i];
            if (overrideIndex == null || overrideIndex.IsNull)
            {
                pathNames[i] = string.Empty;
                continue;
            }

            if (overrideIndex.Load() is UMaterialInterface material)
            {
                loadedMaterials[i] = material;
                pathNames[i] = material.GetPathName();
            }
            else
            {
                pathNames[i] = overrideIndex.Name ?? string.Empty;
            }
        }
        overrideMaterials = loadedMaterials;
        overrideMaterialPathNames = pathNames;
    }

    private static bool CarriesStaticMeshSlot(UObject component)
    {
        if (component.TryGetValue(out FPackageIndex _, "StaticMesh")) return true;
        if (component.TryGetValue(out FPackageIndex restCollection, "RestCollection") &&
            !restCollection.IsNull)
            return true;
        return false;
    }

    // 1:1 of Renderer.cs:565-570: prefer the template's StaticMesh field, fall
    // back to the GeometryCollection RestCollection's first proxy mesh. The
    // out-parameter is non-null only when a usable mesh with materials is
    // resolved, mirroring the `m is { Materials.Length: > 0 }` check at
    // Renderer.cs:572.
    private static bool TryResolveTemplateMesh(UObject component, out UStaticMesh? mesh)
    {
        mesh = null;
        if (!component.TryGetValue(out UStaticMesh directMesh, "StaticMesh"))
        {
            if (component.TryGetValue(out FPackageIndex restCollectionIndex, "RestCollection") &&
                restCollectionIndex.TryLoad(out UGeometryCollection geometryCollection) &&
                geometryCollection.RootProxyData is { ProxyMeshes.Length: > 0 } rootProxyData &&
                rootProxyData.ProxyMeshes[0].TryLoad(out UStaticMesh proxyMesh))
            {
                directMesh = proxyMesh;
            }
            else
            {
                return false;
            }
        }
        if (directMesh is null || directMesh.Materials.Length == 0) return false;
        mesh = directMesh;
        return true;
    }
}
