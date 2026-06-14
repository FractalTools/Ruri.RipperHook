using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.Lights;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Views.Snooper;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Walks every reasonable component-bearing field on an actor and yields one
// PlacedComponent per leaf renderable component the dispatch table can claim.
// "Walks every reasonable component-bearing field" is the linchpin of the
// unified-dispatch design: instead of a per-game branch for "boulders have 13
// static mesh components, this BP_Chochin_lamp has a point light + mesh", the
// resolver enumerates ALL of:
//
//   * BlueprintCreatedComponents[]     — cooked Blueprint actors (BP_Boulder,
//                                        BP_Torii, BP_Chochin_lamp, ...) keep
//                                        every SCS-spawned component here.
//   * InstanceComponents[]             — instance-specific overrides, also the
//                                        only path UInstancedStaticMeshComponent
//                                        actors expose their meshes through.
//   * StaticMeshComponent / Mesh /     — named single-component properties
//     SplineMesh / ComponentTemplate /   on AStaticMeshActor, BPs with one
//     LightMesh / CameraComponent        spotlight / camera, etc.
//   * LandscapeComponents[]            — handled by yielding the actor itself
//                                        because ALandscapeProxy is its own
//                                        landscape "leaf" and the landscape
//                                        exporter rebuilds geometry from the
//                                        proxy + components together.
//
// Duplicates between sources are filtered by object identity (HashSet<UObject>):
// a component referenced by both BlueprintCreatedComponents and a named
// property must yield exactly once. AttachParent-driven world transform is
// computed via SceneTransform.CalculateTransform — exactly the verified
// Renderer's CalculateTransform path, so spline knots, BP child meshes and
// "rope component nested two SceneComponents deep" all land at their preview
// position. LODActor is suppressed at the WorldGlbExporter level (it never
// arrives here), matching Renderer.cs:448.
//
// The resolver only feeds the RENDER layer (IComponentExporter table). The
// lossless layer iterates the same actor list in WorldGlbExporter directly
// because it must write every component regardless of whether anything in the
// render table claims it. Keeping the two passes separate stops resolver
// special-cases (e.g. "skip components without geometry") from silently
// dropping a Niagara or a PostProcessVolume from the JSON layer.
internal static class ComponentResolver
{
    // Property names probed on a cooked actor when looking for "the one
    // singleton component carrying this actor's renderable". Order matches
    // FModel Renderer's ProcessActor probe order (Renderer.cs:578) — first
    // hit wins per name — but we still inspect every match for completeness
    // because cooked BPs occasionally cross-link (e.g. CameraComponent vs.
    // SkeletalMesh slot).
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
        // `seenPriorSource` tracks UObjects that already came from a PRIOR
        // source array (BCC -> IC -> Singletons). It is consulted at the
        // start of each new source so a cross-source duplicate (the same
        // component appearing in both BCC and InstanceComponents) is yielded
        // only once. CRUCIALLY it is NOT consulted for entries INSIDE the
        // CURRENT source — duplicate entries WITHIN a single InstanceComponents
        // array are yielded multiple times because the verified Renderer's
        // foreach (Renderer.cs:537) does the same, and faithful-port parity
        // requires identical fan-out. (Parity self-test: the central foliage
        // actor InstancedFoliageActor_25600_0_0_0 holds 96 InstanceComponents
        // entries that resolve to 48 distinct UObjects; without this
        // distinction the resolver collapses 196094 placements down to 98047.)
        HashSet<UObject> seenPriorSource = new();

        // ----- (1) BlueprintCreatedComponents[] -----------------------------
        // Cooked Blueprint actors store every SCS-spawned component in this
        // array (BP_Boulder=13 static mesh, BP_Torii=18, BP_Ancestral_tree=19,
        // BP_Chochin_lamp = mesh + point light, BP_Fireflies = Niagara + point
        // light, BP_Rope_spline / River_spline = SplineMesh×N). Walking it
        // first means a BP with both a mesh AND a light yields BOTH placements
        // for the table to dispatch on — the prior pipeline only saw whichever
        // named property fired first and missed the rest.
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

        // ----- (2) InstanceComponents[] -------------------------------------
        // Per-instance components — InstancedStaticMeshComponent actors keep
        // their PerInstanceSMData here. We still hand the WHOLE component to
        // the dispatch table; the static-mesh exporter is responsible for the
        // ISM per-instance loop (Renderer.cs:547-555), not the resolver. Note
        // we do NOT dedup within this loop — see comment on seenPriorSource
        // above for why intra-array duplicates must be re-yielded.
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

        // ----- (3) Named single-component properties ------------------------
        // AStaticMeshActor.StaticMeshComponent / ABoulder.Mesh / etc. plus
        // CameraComponent so a cooked CineCameraActor surfaces too. Probed in
        // a single pass so a singleton already covered by BCC or IC is NOT
        // yielded again.
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

        // ----- (4) Landscape ------------------------------------------------
        // ALandscapeProxy carries LandscapeComponents[16] and its own splat /
        // height data; the landscape exporter rebuilds the entire proxy at
        // once via CUE4Parse's LandscapeExporter, so the resolver yields the
        // proxy actor itself as a single PlacedComponent. The landscape
        // exporter's CanExport returns true on `component is ALandscapeProxy`.
        if (actor is ALandscapeProxy landscapeProxy && actor is UObject actorObject && !seenPriorSource.Contains(actorObject))
        {
            yield return new PlacedComponent(landscapeProxy, baseTransform, actor);
        }
    }

    // Load a component package index, swallowing per-component deserialization
    // failures the same way FPackageIndex.TryLoad(out UExport) does — wrapping
    // `Object?.Value` in try/catch. This MUST mirror TryLoad's behaviour because
    // the verified Renderer's InstanceComponents loop uses TryLoad and skips
    // failing entries with `continue`. If we let Load() propagate the
    // exception, an iterator's `yield return` shape would tear down the entire
    // foreach for this actor — losing every subsequent valid component in the
    // SAME array. The Oni_Valley parity self-test caught this (97k missing
    // placements traced to one foliage actor where component #N threw and the
    // remaining 18+ components were silently lost).
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

    // The IComponentExporter table is allowed to inspect a component freely
    // and return false; the resolver still pre-filters obvious non-renderables
    // so the table is consulted only on components that could plausibly be
    // claimed by SOME exporter. Pure SceneComponent and SpringArm carry no
    // renderable contribution today (they are just attach pivots).
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
