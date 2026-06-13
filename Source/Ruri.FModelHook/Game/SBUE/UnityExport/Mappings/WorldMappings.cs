using AssetRipper.Assets.Collections;
using AssetRipper.Primitives;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_1001;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_23;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.WorldPartition;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Ruri.FModelHook.Game.SBUE.UnityExport.Engine;
using System.Numerics;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Mappings;

// UWorld -> a Unity prefab hierarchy. A UE world becomes ONE prefab whose root
// GameObject + Transform parent every placed actor (each placed actor -> one
// child GameObject with Transform, MeshFilter, MeshRenderer). The whole hierarchy
// is registered as a single PrefabHierarchyObject so AssetRipper's stock
// SceneYamlExporter / PrefabExportCollection emits it as one .prefab.
//
// Actor aggregation is data-complete: persistent-level actors, World Partition
// runtime-hash cells (both UWorldPartitionRuntimeSpatialHash and
// UWorldPartitionRuntimeHashSet shapes), generated cell .umap packages, streaming
// sub-levels, and OFPA external actors are all walked. This mirrors the verified
// GLB world exporter (WorldGlbExporter + WorldActorCollector) so the Unity output
// is the same set of placements as the GLB scene.
//
// Per-actor mesh resolution mirrors the GLB path too: actor.InstanceComponents ->
// per-ISMC PerInstanceSMData expansion -> single SMC path -> ComponentTemplate /
// GeometryCollection RestCollection proxy mesh path. AttachParent chains are
// folded down to a single world-space FTransform on the placement node — Unity
// composes per-Transform local TRS automatically, but the UE parent chain lives
// under SCS components we do not emit, so the only correct option is to bake the
// chain into the leaf placement.
//
// All transforms stay in raw Unreal axes (Z-up, centimetres) — consistent with
// the StaticMesh mapping which transcribes vertex data in raw UE space too. A
// uniform UE->Unity basis change is a clean follow-up post-pass; the placement
// DATA here is lossless.
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
        root.TagString = "Untagged";
        root.StaticEditorFlags = 0;
        // Steer the prefab file path: AR's GetBestDirectory/Name falls back here
        // when OverrideDirectory is unset. Keep worlds out of the generic
        // "Assets/GameObject/" bucket so multiple worlds don't all collide on the
        // same filename root.
        root.OriginalDirectory = "Assets/Worlds";
        root.OriginalName = world.Name;
        root.OriginalExtension = "prefab";

        ITransform rootTransform = CreateTransform(collection, root);

        // Aggregate every actor placement source the GLB exporter aggregates:
        // persistent-level + WP cells + generated cell umaps + streaming sub-levels
        // + OFPA external actors. Without this, a UE5 open-world map emits an
        // almost-empty prefab (cooked .umap has barely any actors).
        IReadOnlyList<IPropertyHolder> placedActors = CollectAllActors(world, context);

        foreach (IPropertyHolder placement in placedActors)
        {
            try
            {
                BuildActor(placement, rootTransform, collection, context);
            }
            catch (Exception)
            {
                // One bad actor must not sink the prefab; per-actor exceptions
                // are intentionally swallowed (parity with the GLB exporter).
            }
        }

        // Create the IPrefabInstance for the root only. Do NOT also build the
        // PrefabHierarchyObject ourselves — AR's PrefabProcessor (run by
        // ExportHandler.Process before Export) discovers every IPrefabInstance
        // in the bundle, calls SetPrefabInternal + PrefabHierarchyObject.Create
        // for each unprocessed root, and stamps MainAsset on the whole subtree.
        // If we did that here, PrefabProcessor would do it again and hit
        // SetMainAsset's "Asset already has a main asset assigned" assertion.
        root.CreatePrefabForRoot(collection);
    }

    // Mirrors WorldActorCollector. The GLB exporter takes IFileProvider via
    // constructor; here we recover it from world.Owner.Provider — every UWorld
    // produced by CUE4Parse carries its owning package, and every owning package
    // carries the provider that loaded it.
    private static IReadOnlyList<IPropertyHolder> CollectAllActors(UWorld world, ConversionContext context)
    {
        List<IPropertyHolder> result = new();
        IFileProvider? provider = world.Owner?.Provider;

        AddPersistentLevelActors(world, result);

        if (provider == null)
        {
            return result;
        }

        // The file key (e.g. "Game/Content/Maps/MyMap.umap") differs from the
        // logical /Game/... path; FixPath maps the logical path to the file key
        // family, and stripping the extension gives the prefix WorldActorCollector
        // scans for generated cell umaps + OFPA external actors.
        string? worldPackageName = world.Owner?.Name;
        if (string.IsNullOrEmpty(worldPackageName))
        {
            return result;
        }

        string scanKey;
        try
        {
            scanKey = StripPackageExtension(provider.FixPath(worldPackageName));
        }
        catch (Exception)
        {
            // FixPath can throw on malformed inputs; fall back to embedded-only.
            return result;
        }

        HashSet<string> visitedWorlds = new(StringComparer.OrdinalIgnoreCase);
        if (worldPackageName != null)
        {
            visitedWorlds.Add(worldPackageName);
        }

        ScanProviderFiles(provider, scanKey, out List<string> generatedCellKeys, out List<string> externalActorKeys);

        Queue<UWorld> worldQueue = new();

        foreach (string key in generatedCellKeys)
        {
            if (TryLoadWorld(provider, key, out UWorld? cellWorld) && cellWorld != null)
            {
                worldQueue.Enqueue(cellWorld);
            }
        }

        if (world.PersistentLevel?.Load<ULevel>() is { } persistentLevel)
        {
            foreach (UWorld cellWorld in EnumerateWorldPartitionCellWorlds(persistentLevel))
            {
                worldQueue.Enqueue(cellWorld);
            }
        }
        foreach (UWorld subWorld in EnumerateStreamingLevelWorlds(world))
        {
            worldQueue.Enqueue(subWorld);
        }

        while (worldQueue.Count > 0)
        {
            UWorld current = worldQueue.Dequeue();
            string currentKey = current.Owner?.Name ?? current.GetPathName();
            if (!visitedWorlds.Add(currentKey))
            {
                continue;
            }
            if (current.PersistentLevel?.Load<ULevel>() is not { } level)
            {
                continue;
            }
            AddLevelActors(level, result);
            foreach (UWorld cellWorld in EnumerateWorldPartitionCellWorlds(level))
            {
                worldQueue.Enqueue(cellWorld);
            }
            foreach (UWorld subWorld in EnumerateStreamingLevelWorlds(current))
            {
                worldQueue.Enqueue(subWorld);
            }
        }

        AddExternalActors(provider, externalActorKeys, result);
        return result;
    }

    private static void AddPersistentLevelActors(UWorld world, List<IPropertyHolder> result)
    {
        if (world.PersistentLevel?.Load<ULevel>() is { } level)
        {
            AddLevelActors(level, result);
        }
    }

    private static void AddLevelActors(ULevel level, List<IPropertyHolder> result)
    {
        if (level.Actors == null)
        {
            return;
        }
        foreach (FPackageIndex actorIndex in level.Actors)
        {
            if (actorIndex == null || actorIndex.IsNull)
            {
                continue;
            }
            if (actorIndex.Load() is not { } actor)
            {
                continue;
            }
            // HLOD proxy actors duplicate real geometry at lower detail; skip
            // them to match the FModel preview / GLB exporter.
            if (actor.ExportType == "LODActor")
            {
                continue;
            }
            result.Add(actor);
        }
    }

    private static void AddExternalActors(IFileProvider provider, List<string> keys, List<IPropertyHolder> result)
    {
        foreach (string key in keys)
        {
            if (!TryLoadPackage(provider, key, out IPackage? package) || package == null)
            {
                continue;
            }
            foreach (UObject export in package.GetExports())
            {
                if (export is AActor actor)
                {
                    result.Add(actor);
                }
            }
        }
    }

    private static IEnumerable<UWorld> EnumerateWorldPartitionCellWorlds(ULevel level)
    {
        UObject? runtimeHash = null;
        try
        {
            if (level.WorldSettings != null && !level.WorldSettings.IsNull &&
                level.WorldSettings.Load() is { } worldSettings &&
                worldSettings.GetOrDefault<FPackageIndex>("WorldPartition") is { IsNull: false } wpIndex &&
                wpIndex.Load<UWorldPartition>() is { } worldPartition &&
                worldPartition.RuntimeHash is { IsNull: false } hashIndex)
            {
                runtimeHash = hashIndex.Load();
            }
        }
        catch (Exception)
        {
            yield break;
        }

        if (runtimeHash == null)
        {
            yield break;
        }

        foreach (UObject cell in EnumerateCells(runtimeHash))
        {
            if (cell is not UWorldPartitionRuntimeLevelStreamingCell streamingCell)
            {
                continue;
            }
            FPackageIndex? levelStreamingIndex = streamingCell.LevelStreaming;
            if (levelStreamingIndex == null || levelStreamingIndex.IsNull)
            {
                continue;
            }
            UWorld? cellWorld = null;
            try
            {
                if (levelStreamingIndex.Load<ULevelStreaming>() is { WorldAsset: { } worldAsset } &&
                    worldAsset.TryLoad<UWorld>(out UWorld loaded))
                {
                    cellWorld = loaded;
                }
            }
            catch (Exception)
            {
                cellWorld = null;
            }
            if (cellWorld != null)
            {
                yield return cellWorld;
            }
        }
    }

    private static IEnumerable<UObject> EnumerateCells(UObject runtimeHash)
    {
        switch (runtimeHash)
        {
            case UWorldPartitionRuntimeSpatialHash spatialHash:
                foreach (FSpatialHashStreamingGrid grid in spatialHash.StreamingGrids ?? [])
                {
                    foreach (FSpatialHashStreamingGridLevel gridLevel in grid.GridLevels ?? [])
                    {
                        foreach (FSpatialHashStreamingGridLayerCell layerCell in gridLevel.LayerCells ?? [])
                        {
                            foreach (FPackageIndex cellIndex in layerCell.GridCells ?? [])
                            {
                                if (cellIndex is { IsNull: false } && cellIndex.Load() is { } cell)
                                {
                                    yield return cell;
                                }
                            }
                        }
                    }
                }
                break;
            case UWorldPartitionRuntimeHashSet hashSet:
                foreach (FRuntimePartitionStreamingData data in hashSet.RuntimeStreamingData ?? [])
                {
                    foreach (UObject cell in LoadCells(data.SpatiallyLoadedCells))
                    {
                        yield return cell;
                    }
                    foreach (UObject cell in LoadCells(data.NonSpatiallyLoadedCells))
                    {
                        yield return cell;
                    }
                }
                break;
        }
    }

    private static IEnumerable<UObject> LoadCells(FPackageIndex[]? cells)
    {
        foreach (FPackageIndex cellIndex in cells ?? [])
        {
            if (cellIndex is { IsNull: false } && cellIndex.Load() is { } cell)
            {
                yield return cell;
            }
        }
    }

    private static IEnumerable<UWorld> EnumerateStreamingLevelWorlds(UWorld world)
    {
        foreach (FPackageIndex? streamingIndex in world.StreamingLevels ?? [])
        {
            if (streamingIndex is null || streamingIndex.IsNull)
            {
                continue;
            }
            UWorld? subWorld = null;
            try
            {
                if (streamingIndex.Load<ULevelStreaming>() is { WorldAsset: { } worldAsset } &&
                    worldAsset.TryLoad<UWorld>(out UWorld loaded))
                {
                    subWorld = loaded;
                }
            }
            catch (Exception)
            {
                subWorld = null;
            }
            if (subWorld != null)
            {
                yield return subWorld;
            }
        }
    }

    // 1:1 with WorldActorCollector.ScanProviderFiles: one pass over the provider
    // file table, bucketing generated cell maps (.umap under "<mainKey>/") and
    // OFPA actor packages (.uasset under the matching __ExternalActors__ prefix).
    private static void ScanProviderFiles(IFileProvider provider, string scanKey, out List<string> generatedCellKeys, out List<string> externalActorKeys)
    {
        generatedCellKeys = new List<string>();
        externalActorKeys = new List<string>();

        string generatedCellPrefix = scanKey + "/";
        string? externalActorPrefix = BuildExternalActorPrefix(scanKey);

        foreach (string key in provider.Files.Keys)
        {
            if (key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                if (key.StartsWith(generatedCellPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    generatedCellKeys.Add(key);
                }
            }
            else if (externalActorPrefix != null &&
                     key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                     key.StartsWith(externalActorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                externalActorKeys.Add(key);
            }
        }
    }

    private static string? BuildExternalActorPrefix(string scanKey)
    {
        const string contentSegment = "/Content/";
        int index = scanKey.IndexOf(contentSegment, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }
        string head = scanKey[..(index + "/Content".Length)];
        string rest = scanKey[(index + contentSegment.Length)..];
        return $"{head}/__ExternalActors__/{rest}/";
    }

    private static bool TryLoadWorld(IFileProvider provider, string packagePath, out UWorld? world)
    {
        world = null;
        if (!TryLoadPackage(provider, packagePath, out IPackage? package) || package == null)
        {
            return false;
        }
        foreach (UObject export in package.GetExports())
        {
            if (export is UWorld loaded)
            {
                world = loaded;
                return true;
            }
        }
        return false;
    }

    private static bool TryLoadPackage(IFileProvider provider, string key, out IPackage? package)
    {
        package = null;
        try
        {
            if (!provider.Files.TryGetValue(key, out var gameFile))
            {
                return false;
            }
            package = provider.LoadPackage(gameFile);
            return package != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string StripPackageExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash ? path[..dot] : path;
    }

    // 1:1 with WorldGlbExporter.ProcessActor (Renderer.WorldMesh / ProcessMesh
    // port). Three cooked-actor shapes, in priority order:
    //   1) actor.InstanceComponents -> 1..N components, each may be an ISMC with
    //      PerInstanceSMData expanded into N placements;
    //   2) actor.ComponentTemplate -> a template UStaticMeshComponent or a
    //      template with RestCollection -> UGeometryCollection.RootProxyData.ProxyMeshes[0];
    //   3) the actor itself exposes one of {StaticMeshComponent, ComponentTemplate,
    //      StaticMesh, Mesh, LightMesh, SplineMesh} -> a single SMC placement.
    private static void BuildActor(IPropertyHolder actor, ITransform rootTransform, ProcessedAssetCollection collection, ConversionContext context)
    {
        if (actor is not UObject actorObject)
        {
            return;
        }

        if (actor.TryGetValue(out FPackageIndex[] instanceComponents, "InstanceComponents") && instanceComponents.Length > 0)
        {
            int instanceComponentIndex = 0;
            foreach (FPackageIndex componentIndex in instanceComponents)
            {
                if (componentIndex == null || componentIndex.IsNull)
                {
                    continue;
                }
                if (!componentIndex.TryLoad(out UStaticMeshComponent component))
                {
                    continue;
                }
                if (!component.GetStaticMesh().TryLoad(out UStaticMesh mesh) || mesh.Materials.Length == 0)
                {
                    continue;
                }
                FTransform componentRelation = ComposeAttachChainTransform(component, FTransform.Identity);

                if (component is UInstancedStaticMeshComponent instanced &&
                    instanced.PerInstanceSMData is { Length: > 0 } perInstanceData)
                {
                    for (int i = 0; i < perInstanceData.Length; i++)
                    {
                        FTransform local = perInstanceData[i].TransformData;
                        FTransform world = MultiplyTransform(local, componentRelation);
                        EmitPlacement(actorObject, component, mesh, world, $"{actorObject.Name}_ISMC{instanceComponentIndex}_Inst{i}", rootTransform, collection, context);
                    }
                }
                else
                {
                    EmitPlacement(actorObject, component, mesh, componentRelation, $"{actorObject.Name}_IC{instanceComponentIndex}", rootTransform, collection, context);
                }
                instanceComponentIndex++;
            }
            return;
        }

        if (actor.TryGetValue(out FPackageIndex componentTemplateIndex, "ComponentTemplate") &&
            componentTemplateIndex.TryLoad(out UObject template))
        {
            UStaticMesh? templateMesh = null;
            if (template.TryGetValue(out UStaticMesh staticMeshFromTemplate, "StaticMesh"))
            {
                templateMesh = staticMeshFromTemplate;
            }
            else if (template.TryGetValue(out FPackageIndex restCollectionIndex, "RestCollection") &&
                     restCollectionIndex.TryLoad(out UGeometryCollection geometryCollection) &&
                     geometryCollection.RootProxyData is { ProxyMeshes.Length: > 0 } rootProxyData &&
                     rootProxyData.ProxyMeshes[0] is { } proxyMeshRef &&
                     proxyMeshRef.TryLoad(out UStaticMesh proxyMesh))
            {
                templateMesh = proxyMesh;
            }

            if (templateMesh is { Materials.Length: > 0 })
            {
                FTransform placement = ComposeAttachChainTransform(template, FTransform.Identity);
                EmitPlacement(actorObject, template, templateMesh, placement, actorObject.Name, rootTransform, collection, context);
            }
            return;
        }

        if (actor.TryGetValue(out FPackageIndex staticMeshComponentIndex, "StaticMeshComponent", "ComponentTemplate", "StaticMesh", "Mesh", "LightMesh", "SplineMesh") &&
            staticMeshComponentIndex.TryLoad(out UStaticMeshComponent staticMeshComponent) &&
            staticMeshComponent.GetStaticMesh().TryLoad(out UStaticMesh actorMesh) &&
            actorMesh.Materials.Length > 0)
        {
            FTransform placement = ComposeAttachChainTransform(staticMeshComponent, FTransform.Identity);
            EmitPlacement(actorObject, staticMeshComponent, actorMesh, placement, actorObject.Name, rootTransform, collection, context);
        }
    }

    private static void EmitPlacement(UObject actor, UObject component, UStaticMesh mesh, FTransform placement, string name, ITransform rootTransform, ProcessedAssetCollection collection, ConversionContext context)
    {
        if (context.ConvertAs<IMesh>(mesh) is not { } unityMesh)
        {
            return;
        }

        IGameObject gameObject = collection.CreateGameObject();
        gameObject.Name = string.IsNullOrEmpty(name) ? "Actor" : name;
        gameObject.SetIsActive(true);
        gameObject.Layer = 0;
        gameObject.TagString = "Untagged";
        gameObject.StaticEditorFlags = 0;

        ITransform transform = CreateTransform(collection, gameObject);
        ApplyTransform(transform, placement);
        transform.Father_C4P = rootTransform;
        rootTransform.Children_C4P.Add(transform);

        IMeshFilter meshFilter = collection.CreateMeshFilter();
        meshFilter.GameObjectP = gameObject;
        meshFilter.MeshP = unityMesh;
        gameObject.AddComponent(ClassIDType.MeshFilter, meshFilter);

        IMeshRenderer meshRenderer = collection.CreateMeshRenderer();
        meshRenderer.GameObject_C25P = gameObject;
        AddMaterials(meshRenderer, mesh, component, context);
        InitializeRendererDefaults(meshRenderer);
        gameObject.AddComponent(ClassIDType.MeshRenderer, meshRenderer);
    }

    private static ITransform CreateTransform(ProcessedAssetCollection collection, IGameObject gameObject)
    {
        ITransform transform = collection.CreateTransform();
        transform.InitializeDefault();
        transform.GameObject_C4P = gameObject;
        gameObject.AddComponent(ClassIDType.Transform, transform);
        return transform;
    }

    private static void ApplyTransform(ITransform transform, FTransform placement)
    {
        FVector translation = placement.Translation;
        FQuat rotation = placement.Rotation;
        FVector scale = placement.Scale3D;

        transform.LocalPosition_C4.SetValues(translation.X, translation.Y, translation.Z);
        transform.LocalRotation_C4.SetValues(rotation.X, rotation.Y, rotation.Z, rotation.W);
        transform.LocalScale_C4.SetValues(scale.X, scale.Y, scale.Z);

        if (transform.Has_LocalEulerAnglesHint_C4())
        {
            // EditorFormatConverterAsync.Convert(ITransform) does this on Release-flag
            // collections; ProcessedAssetCollection has NoTransferInstructionFlags
            // (not Release), so the processor filters it out — we must compute the
            // hint manually or Unity's inspector shows zeroed Euler angles for the
            // node despite a non-identity quaternion.
            Quaternion quaternion = new(rotation.X, rotation.Y, rotation.Z, rotation.W);
            Vector3 eulerHints = QuaternionToEulerHint(quaternion);
            transform.LocalEulerAnglesHint_C4.SetValues(eulerHints.X, eulerHints.Y, eulerHints.Z);
        }
    }

    // Walks AttachParent like SceneTransform.CalculateTransform: a component's
    // placement folds in every parent SceneComponent's local TRS. Returns the
    // fully-composed local-to-world TRS for the leaf component. We can't model
    // the SCS chain as Unity sub-Transforms because we don't emit SCS components
    // as GameObjects — baking is the only correct option.
    private static FTransform ComposeAttachChainTransform(IPropertyHolder component, FTransform relation)
    {
        FTransform composed = relation;
        if (component.TryGetValue(out FPackageIndex attachParent, "AttachParent") &&
            attachParent.TryLoad(out UObject parent))
        {
            composed = ComposeAttachChainTransform(parent, relation);
        }

        FVector localPosition = component.GetOrDefault("RelativeLocation", FVector.ZeroVector);
        FRotator localRotation = component.GetOrDefault("RelativeRotation", FRotator.ZeroRotator);
        FVector localScale = component.GetOrDefault("RelativeScale3D", FVector.OneVector);

        FTransform local = new()
        {
            Translation = localPosition,
            Rotation = localRotation.Quaternion(),
            Scale3D = localScale,
        };

        return MultiplyTransform(local, composed);
    }

    // child = local applied in parent's frame: world.T = parent.T + parent.R *
    // (parent.S * local.T); world.R = parent.R * local.R; world.S = parent.S *
    // local.S. FTransform has no Multiply, so spell it out here.
    private static FTransform MultiplyTransform(FTransform local, FTransform parent)
    {
        FVector parentScale = parent.Scale3D;
        FVector scaledLocalPosition = new(
            local.Translation.X * parentScale.X,
            local.Translation.Y * parentScale.Y,
            local.Translation.Z * parentScale.Z);
        Vector3 rotated = RotateByQuaternion(scaledLocalPosition, parent.Rotation);
        FVector worldPosition = new(
            parent.Translation.X + rotated.X,
            parent.Translation.Y + rotated.Y,
            parent.Translation.Z + rotated.Z);
        FQuat worldRotation = MultiplyQuaternion(parent.Rotation, local.Rotation);
        FVector worldScale = new(
            parentScale.X * local.Scale3D.X,
            parentScale.Y * local.Scale3D.Y,
            parentScale.Z * local.Scale3D.Z);
        return new FTransform
        {
            Translation = worldPosition,
            Rotation = worldRotation,
            Scale3D = worldScale,
        };
    }

    private static Vector3 RotateByQuaternion(FVector vector, FQuat quaternion)
    {
        Vector3 v = new(vector.X, vector.Y, vector.Z);
        Quaternion q = new(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
        return Vector3.Transform(v, q);
    }

    private static FQuat MultiplyQuaternion(FQuat parent, FQuat child)
    {
        // UE FQuat multiplication: a*b means b is applied first, then a (same
        // convention as System.Numerics.Quaternion).
        Quaternion p = new(parent.X, parent.Y, parent.Z, parent.W);
        Quaternion c = new(child.X, child.Y, child.Z, child.W);
        Quaternion r = p * c;
        return new FQuat(r.X, r.Y, r.Z, r.W);
    }

    // System.Numerics-free port of AssetRipper's Quaternion -> Euler hint
    // (degrees, XYZ order) — duplicates EditorFormatConverterAsync's call into
    // QuaternionExtensions.ToEulerAngle(useDegrees: true), which we cannot
    // invoke because that extension lives on AR's internal Numerics namespace.
    private static Vector3 QuaternionToEulerHint(Quaternion quaternion)
    {
        Quaternion q = Quaternion.Normalize(quaternion);
        const float radToDeg = 180f / MathF.PI;

        // Tait-Bryan ZYX -> Unity-style XYZ Euler hint expected by Unity's inspector.
        float sinPitch = 2f * (q.W * q.X - q.Y * q.Z);
        float pitch;
        float yaw;
        float roll;
        if (MathF.Abs(sinPitch) >= 0.999999f)
        {
            // Gimbal lock; fall back to a stable branch.
            pitch = MathF.CopySign(MathF.PI * 0.5f, sinPitch);
            yaw = 2f * MathF.Atan2(q.Y, q.W);
            roll = 0f;
        }
        else
        {
            pitch = MathF.Asin(sinPitch);
            yaw = MathF.Atan2(2f * (q.W * q.Y + q.X * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
            roll = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.X * q.X + q.Z * q.Z));
        }
        return new Vector3(pitch * radToDeg, yaw * radToDeg, roll * radToDeg);
    }

    // Materials must be preserved IN ORDER, with null PPtrs for missing slots —
    // Unity's MeshRenderer assigns Materials[i] to submesh i, so a skipped entry
    // shifts every later submesh to the wrong material. OverrideMaterials on the
    // component, when present, take precedence per slot (FModel preview parity).
    private static void AddMaterials(IMeshRenderer renderer, UStaticMesh mesh, UObject component, ConversionContext context)
    {
        FPackageIndex[]? overrideMaterials = null;
        component.TryGetValue(out overrideMaterials, "OverrideMaterials");

        for (int slot = 0; slot < mesh.Materials.Length; slot++)
        {
            IMaterial? slotMaterial = null;
            if (overrideMaterials != null && slot < overrideMaterials.Length &&
                overrideMaterials[slot] is { IsNull: false } overrideIndex &&
                overrideIndex.TryLoad(out UMaterialInterface overrideMat) &&
                context.ConvertAs<IMaterial>(overrideMat) is { } unityOverride)
            {
                slotMaterial = unityOverride;
            }
            else if (mesh.Materials[slot] is { } baseRef &&
                     baseRef.Load<UMaterialInterface>() is { } baseMat &&
                     context.ConvertAs<IMaterial>(baseMat) is { } unityBase)
            {
                slotMaterial = unityBase;
            }
            // Append even when null: Add(null) writes an empty PPtr {fileID: 0}
            // — keeps the slot index aligned with mesh submesh index.
            renderer.Materials_C25P.Add(slotMaterial);
        }
    }

    // EditorFormatProcessor filters by Collection.Flags.IsRelease(); our
    // ProcessedAssetCollection has NoTransferInstructionFlags, so the IsRelease
    // check is false and EditorFormatConverter.Convert(renderer) NEVER runs on
    // our renderers. Without these defaults the renderer YAML has m_Enabled: 0,
    // no shadow casting, no lightmap defaults — Unity loads a "disabled garbage"
    // renderer. So we set the exact values EditorFormatConverter would have set,
    // plus the runtime defaults that processor inherits from the Release pass
    // (Enabled, CastShadows, ReceiveShadows, LightmapTilingOffset).
    private static void InitializeRendererDefaults(IMeshRenderer renderer)
    {
        // Runtime-fundamental: must be enabled or the renderer is dead-on-arrival.
        renderer.Enabled = true;
        renderer.CastShadows_Byte = (byte)ShadowCastingMode.On;
        renderer.CastShadows_Boolean = true;
        renderer.ReceiveShadows_Byte = 1;
        renderer.ReceiveShadows_Boolean = true;
        renderer.LightmapIndex_Byte = byte.MaxValue;
        renderer.LightmapIndex_UInt16 = ushort.MaxValue;
        renderer.LightmapIndexDynamic = ushort.MaxValue;
        // Vector4f LightmapTilingOffset default = (1, 1, 0, 0) (scale=1,1 offset=0,0).
        renderer.LightmapTilingOffset.X = 1f;
        renderer.LightmapTilingOffset.Y = 1f;
        renderer.LightmapTilingOffset.Z = 0f;
        renderer.LightmapTilingOffset.W = 0f;
        if (renderer.LightmapTilingOffsetDynamic is { } dynamicTilingOffset)
        {
            dynamicTilingOffset.X = 1f;
            dynamicTilingOffset.Y = 1f;
            dynamicTilingOffset.Z = 0f;
            dynamicTilingOffset.W = 0f;
        }

        // Mirror of EditorFormatConverter.Convert(renderer): editor-side
        // lighting-bake defaults Unity expects in every prefab.
        renderer.ScaleInLightmap_C25 = 1f;
        renderer.ReceiveGI_C25 = (int)ReceiveGI.Lightmaps;
        renderer.PreserveUVs_C25 = false;
        renderer.IgnoreNormalsForChartDetection_C25 = false;
        renderer.ImportantGI_C25 = false;
        renderer.StitchLightmapSeams_C25 = false;
        renderer.SelectedEditorRenderState_C25 = (int)(EditorSelectedRenderState)3;
        renderer.MinimumChartSize_C25 = 4;
        renderer.AutoUVMaxDistance_C25 = 0.5f;
        renderer.AutoUVMaxAngle_C25 = 89f;
        renderer.LightmapParameters_C25P = null;

        // Runtime defaults the AR Release-flag pipeline inherits implicitly but
        // we have to set explicitly because we don't go through that pipeline.
        if (renderer.Has_DynamicOccludee())
        {
            renderer.DynamicOccludee = 1;
        }
        if (renderer.Has_LightProbeUsage())
        {
            renderer.LightProbeUsage = (byte)LightProbeUsage.BlendProbes;
        }
        renderer.UseLightProbes = true;
        if (renderer.Has_MotionVectors())
        {
            renderer.MotionVectors = (byte)MotionVectorGenerationMode.Object;
        }
        if (Has_ReflectionProbeUsage_Byte_Safe(renderer))
        {
            renderer.ReflectionProbeUsage_C25_Byte = (byte)ReflectionProbeUsage.BlendProbes;
        }
        if (Has_ReflectionProbeUsage_Int32_Safe(renderer))
        {
            renderer.ReflectionProbeUsage_C25_Int32 = (int)ReflectionProbeUsage.BlendProbes;
        }
        if (renderer.Has_RenderingLayerMask())
        {
            renderer.RenderingLayerMask = 1u;
        }
        if (renderer.Has_RendererPriority())
        {
            renderer.RendererPriority = 0;
        }
        renderer.SortingLayer = 0;
        renderer.SortingOrder = 0;
        renderer.SortingLayerID_UInt32 = 0u;
        renderer.SortingLayerID_Int32 = 0;
    }

    // The two Has_ReflectionProbeUsage_* helpers exist on IRenderer (sealed
    // group, _C25 suffix) but are not promoted to IMeshRenderer's bare-name
    // surface; reach them through the base interface.
    private static bool Has_ReflectionProbeUsage_Byte_Safe(IMeshRenderer renderer)
    {
        IRenderer baseRenderer = renderer;
        return baseRenderer.Has_ReflectionProbeUsage_C25_Byte();
    }

    private static bool Has_ReflectionProbeUsage_Int32_Safe(IMeshRenderer renderer)
    {
        IRenderer baseRenderer = renderer;
        return baseRenderer.Has_ReflectionProbeUsage_C25_Int32();
    }
}
