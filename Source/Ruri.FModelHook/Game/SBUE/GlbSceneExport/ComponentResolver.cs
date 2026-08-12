using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.Lights;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

internal static class ComponentResolver
{
    private static readonly string[] SingletonComponentPropertyNames =
    {
        "StaticMeshComponent",
        "ComponentTemplate",
        "StaticMesh",
        "Mesh",
        "LightMesh",
        "SplineMesh",
        "CameraComponent",
        "CineCameraComponent",
        "LightComponent",
    };

    public static IEnumerable<PlacedComponent> Resolve(IPropertyHolder actor, Transform baseTransform)
    {
        HashSet<UObject> seenPriorSource = new();

        HashSet<UObject> seenInBcc = new();
        if (actor.TryGetValue(out FPackageIndex[] blueprintCreatedComponents, "BlueprintCreatedComponents"))
        {
            foreach (var componentIndex in blueprintCreatedComponents)
            {
                if (componentIndex == null || componentIndex.IsNull) continue;
                if (!TryLoadObject(componentIndex, out UObject? component) || component is null) continue;
                if (seenPriorSource.Contains(component)) continue;
                if (!IsRenderableLeaf(component)) continue;
                seenInBcc.Add(component);
                Transform worldTransform = SceneTransform.CalculateTransform(component, baseTransform);
                yield return new PlacedComponent(component, worldTransform, actor);
            }
            foreach (var component in seenInBcc) seenPriorSource.Add(component);
        }

        HashSet<UObject> seenInIc = new();
        if (actor.TryGetValue(out FPackageIndex[] instanceComponents, "InstanceComponents"))
        {
            foreach (var componentIndex in instanceComponents)
            {
                if (componentIndex == null || componentIndex.IsNull) continue;
                if (!TryLoadObject(componentIndex, out UObject? component) || component is null) continue;
                if (seenPriorSource.Contains(component)) continue;
                if (!IsRenderableLeaf(component)) continue;
                seenInIc.Add(component);
                Transform worldTransform = SceneTransform.CalculateTransform(component, baseTransform);
                yield return new PlacedComponent(component, worldTransform, actor);
            }
            foreach (var component in seenInIc) seenPriorSource.Add(component);
        }

        HashSet<UObject> seenInSingletons = new();
        foreach (string propertyName in SingletonComponentPropertyNames)
        {
            if (!actor.TryGetValue(out FPackageIndex componentIndex, propertyName)) continue;
            if (componentIndex == null || componentIndex.IsNull) continue;
            if (!TryLoadObject(componentIndex, out UObject? component) || component is null) continue;
            if (seenPriorSource.Contains(component)) continue;
            if (seenInSingletons.Contains(component)) continue;
            if (!IsRenderableLeaf(component)) continue;
            seenInSingletons.Add(component);
            Transform worldTransform = SceneTransform.CalculateTransform(component, baseTransform);
            yield return new PlacedComponent(component, worldTransform, actor);
        }
        foreach (var component in seenInSingletons) seenPriorSource.Add(component);

        if (actor is ALandscapeProxy landscapeProxy && actor is UObject actorObject && !seenPriorSource.Contains(actorObject))
        {
            yield return new PlacedComponent(landscapeProxy, baseTransform, actor);
        }
    }

    private static bool TryLoadObject(CUE4Parse.UE4.Objects.UObject.FPackageIndex componentIndex, out UObject? component)
    {
        try
        {
            component = componentIndex.Load() as UObject;
            return component != null;
        }
        catch
        {
            component = null;
            return false;
        }
    }

    private static bool IsRenderableLeaf(UObject component)
    {
        switch (component)
        {
            case USpringArmComponent:
                return false;
        }
        return true;
    }
}
