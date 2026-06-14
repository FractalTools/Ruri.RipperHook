using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// One exporter per component family (static mesh, spline mesh, light, camera,
// landscape, ...). Registered in WorldGlbExporter's table in priority order:
// the first CanExport that returns true wins for a given PlacedComponent.
//
// Priority matters because USplineMeshComponent : UStaticMeshComponent — if
// the static-mesh exporter is consulted first it will swallow every spline,
// then emit straight static meshes instead of spline-deformed ones. So the
// table must list spline BEFORE static. Same shape applies to anything that
// derives from a more general component class.
//
// The exporter writes into the shared GlbSceneContext (scene builder, mesh
// cache, light builder, material registry, extras attribution) so all
// pipelines stay welded to one scene graph. Exporters never construct their
// own SharpGLTF SceneBuilder and never open the file system — they call the
// `Add*` helpers on the context and the context owns part-flushing / writes.
public interface IComponentExporter
{
    // Pure type/marker test — no I/O, no allocation. Called for every
    // PlacedComponent in priority order, so cheap. The static-mesh exporter
    // also rejects USplineMeshComponent here so wherever the table is reordered
    // it still cannot accidentally consume a spline.
    bool CanExport(UObject component);

    // Build the geometry / light / camera / landscape contribution for this
    // placement and append it to the shared scene through the context. Per-
    // instance loops (UInstancedStaticMeshComponent.PerInstanceSMData) live
    // inside the static-mesh exporter, not in the resolver, so the resolver
    // does not need to know about ISM cardinality.
    void Export(in PlacedComponent placed, GlbSceneContext context);
}
