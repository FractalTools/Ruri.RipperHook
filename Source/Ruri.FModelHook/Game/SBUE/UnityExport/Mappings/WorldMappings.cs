using AssetRipper.Assets.Collections;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Primitives;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

// UWorld -> a Unity prefab hierarchy. Unlike the 1:1 asset mappings, a world
// produces MANY Unity objects (a root GameObject plus one GameObject + Transform
// + MeshFilter + MeshRenderer per placed actor), which are exported TOGETHER as a
// single .prefab (registered as one export group) rather than one .asset each.
// The referenced meshes/materials still convert + export as standalone assets and
// the renderer components point at them by GUID through the shared context.
//
// Only the actors cooked directly into the persistent level are walked here; full
// World Partition cell / One-File-Per-Actor aggregation needs the file provider
// (see WorldActorCollector) and is a documented follow-up. Transforms are kept in
// raw Unreal space, consistent with the mesh export.
public static class WorldMappings
{
    public static void Register()
    {
        MapperRegistry.Map<UWorld, IGameObject>(collection => collection.CreateGameObject())
            .Set(t => t.Name, s => new Utf8String(s.Name))
            .After(BuildPrefab);
    }

    private static void BuildPrefab(UWorld world, IGameObject root, ConversionContext context)
    {
        ProcessedAssetCollection collection = context.Collection;
        root.SetIsActive(true);
        root.Layer = 0;
        ITransform rootTransform = NewTransform(collection, root);

        if (world.PersistentLevel?.Load<ULevel>() is { Actors: { } actors })
        {
            foreach (FPackageIndex actorIndex in actors)
            {
                if (actorIndex is null || actorIndex.IsNull)
                    continue;
                if (actorIndex.Load() is not { } actor || actor.ExportType == "LODActor")
                    continue;
                TryBuildActor(actor, rootTransform, collection, context);
            }
        }

        // Wrap the hierarchy as one prefab export unit.
        IPrefabInstance prefab = root.CreatePrefabForRoot(collection);
        PrefabHierarchyObject hierarchy = PrefabHierarchyObject.Create(collection, root, prefab);
        context.RegisterExportGroup(new PrefabExportCollection(new SceneYamlExporter(), hierarchy));
    }

    private static void TryBuildActor(UObject actor, ITransform rootTransform, ProcessedAssetCollection collection, ConversionContext context)
    {
        if (!TryResolveStaticMesh(actor, out UStaticMesh? mesh, out UObject? component) || mesh is null || component is null)
            return;
        if (context.ConvertAs<IMesh>(mesh) is not { } unityMesh)
            return;

        IGameObject gameObject = collection.CreateGameObject();
        gameObject.Name = string.IsNullOrEmpty(actor.Name) ? "Actor" : actor.Name;
        gameObject.SetIsActive(true);
        gameObject.Layer = 0;

        ITransform transform = NewTransform(collection, gameObject);
        SetLocalTransform(transform, component);
        transform.Father_C4P = rootTransform;
        rootTransform.Children_C4P.Add(transform);

        IMeshFilter meshFilter = collection.CreateMeshFilter();
        meshFilter.GameObjectP = gameObject;
        meshFilter.MeshP = unityMesh;
        gameObject.AddComponent(ClassIDType.MeshFilter, meshFilter);

        IMeshRenderer meshRenderer = collection.CreateMeshRenderer();
        meshRenderer.GameObject_C25P = gameObject;
        AddMaterials(meshRenderer, mesh, context);
        gameObject.AddComponent(ClassIDType.MeshRenderer, meshRenderer);
    }

    // Mirrors the GLB exporter's per-actor static-mesh resolution (the common
    // single-component case; instanced components are a follow-up).
    private static bool TryResolveStaticMesh(UObject actor, out UStaticMesh? mesh, out UObject? component)
    {
        mesh = null;
        component = null;
        if (actor.TryGetValue(out FPackageIndex componentIndex, "StaticMeshComponent", "ComponentTemplate", "StaticMesh", "Mesh") &&
            componentIndex.TryLoad(out UStaticMeshComponent staticMeshComponent) &&
            staticMeshComponent.GetStaticMesh().TryLoad(out UStaticMesh loadedMesh) &&
            loadedMesh.Materials.Length > 0)
        {
            mesh = loadedMesh;
            component = staticMeshComponent;
            return true;
        }
        return false;
    }

    private static ITransform NewTransform(ProcessedAssetCollection collection, IGameObject gameObject)
    {
        ITransform transform = collection.CreateTransform();
        transform.InitializeDefault();
        transform.GameObject_C4P = gameObject;
        gameObject.AddComponent(ClassIDType.Transform, transform);
        return transform;
    }

    private static void SetLocalTransform(ITransform transform, UObject component)
    {
        FVector location = component.GetOrDefault("RelativeLocation", FVector.ZeroVector);
        FRotator rotation = component.GetOrDefault("RelativeRotation", FRotator.ZeroRotator);
        FVector scale = component.GetOrDefault("RelativeScale3D", FVector.OneVector);
        FQuat quaternion = rotation.Quaternion();

        transform.LocalPosition_C4.SetValues(location.X, location.Y, location.Z);
        transform.LocalRotation_C4.SetValues(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
        transform.LocalScale_C4.SetValues(scale.X, scale.Y, scale.Z);
    }

    private static void AddMaterials(IMeshRenderer renderer, UStaticMesh mesh, ConversionContext context)
    {
        foreach (ResolvedObject? materialReference in mesh.Materials)
        {
            if (materialReference?.Load<UMaterialInterface>() is { } material &&
                context.ConvertAs<IMaterial>(material) is { } unityMaterial)
            {
                renderer.Materials_C25P.Add(unityMaterial);
            }
        }
    }
}
