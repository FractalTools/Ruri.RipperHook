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

public sealed class StaticMeshComponentExporter : IComponentExporter
{
    public bool CanExport(UObject component)
    {
        if (component is USplineMeshComponent) return false;
        if (component is UStaticMeshComponent) return true;
        return CarriesStaticMeshSlot(component);
    }

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        UObject component = placed.Component;
        Transform worldTransform = placed.WorldTransform;
        IPropertyHolder ownerActor = placed.OwnerActor;

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

        _ = ownerActor;
    }

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
