using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Export.UnityProjects;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.UnityExport.Engine;

// The reentrant conversion state for one export session: the target collection
// plus a source-identity cache so each UE object converts to exactly one Unity
// object no matter how many times it is referenced. Cross-object references
// (material -> texture, mesh -> material, actor -> mesh, ...) all flow through
// Convert()/Ptr() here — which is why mapping source lambdas can take a context.
public sealed class ConversionContext
{
    private readonly Dictionary<UObject, IUnityObjectBase?> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly List<IUnityObjectBase> _ordered = new();
    private readonly List<IExportCollection> _exportGroups = new();

    public ConversionContext(ProcessedAssetCollection collection) => Collection = collection;

    public ProcessedAssetCollection Collection { get; }

    // Every successfully-converted object, in creation order — the export worklist.
    public IReadOnlyList<IUnityObjectBase> Converted => _ordered;

    // Multi-object export units (a prefab / scene built by an aggregating mapping
    // like World): these objects are written together into one file instead of one
    // .asset each. The session exports these groups and skips their members from the
    // per-asset pass.
    public IReadOnlyList<IExportCollection> ExportGroups => _exportGroups;

    public void RegisterExportGroup(IExportCollection group) => _exportGroups.Add(group);

    // Convert one UE object to its Unity counterpart, deduplicated by source
    // identity. The created object is cached BEFORE its fields are populated so a
    // reference cycle (A -> B -> A) terminates on the already-created instance.
    // Misses are cached too, so an unmapped referenced object isn't retried.
    public IUnityObjectBase? Convert(UObject? source)
    {
        if (source == null) return null;
        if (_cache.TryGetValue(source, out IUnityObjectBase? existing)) return existing;

        IUnityObjectMapping? mapping = MapperRegistry.Find(source.GetType());
        if (mapping == null)
        {
            _cache[source] = null;
            return null;
        }

        IUnityObjectBase created = mapping.Create(this);
        _cache[source] = created;
        _ordered.Add(created);

        try
        {
            mapping.Populate(source, created, this);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"mapping {mapping.SourceType.Name} failed on UE asset '{source.Name}' ({source.GetType().Name}): {ex.Message}", ex);
        }
        return created;
    }

    // Typed convenience over Convert that returns null instead of throwing on a
    // type mismatch — handy when a UE field's declared type is broader than the
    // mapping target.
    public TDst? ConvertAs<TDst>(UObject? source) where TDst : class, IUnityObjectBase
        => Convert(source) as TDst;

    // A PPtr to the Unity counterpart of a referenced UE object (converted on
    // demand, deduplicated). Returns a null pointer ({fileID: 0}) when the
    // reference is null or its type has no mapping. All synthetic assets live in
    // one collection, so this stays a local fileID.
    public PPtr<T> Ptr<T>(UObject? source) where T : IUnityObjectBase
        => Convert(source) is T converted ? Collection.ForceCreatePPtr(converted) : default;
}
