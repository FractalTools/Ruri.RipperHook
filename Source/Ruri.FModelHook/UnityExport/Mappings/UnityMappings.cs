namespace Ruri.FModelHook.UnityExport.Mappings;

// Single entry point that registers every UE -> Unity mapping family exactly
// once (idempotent + thread-safe). Each phase adds one line here; the registry
// itself never changes.
public static class UnityMappings
{
    private static readonly object _gate = new();
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered) return;
        lock (_gate)
        {
            if (_registered) return;
            TextureMappings.Register();
            // Phase 2+: MaterialMappings / StaticMeshMappings / SkeletalMeshMappings /
            //           AnimationMappings / WorldMappings register here.
            _registered = true;
        }
    }
}
