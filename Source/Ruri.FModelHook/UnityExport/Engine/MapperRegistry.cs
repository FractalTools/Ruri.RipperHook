using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.UnityExport.Engine;

// The central UE-type -> Unity-mapping table. Mappings register here once at
// startup; conversion is a dictionary lookup walked up the UObject base chain.
// Adding an asset family is one Map<,>() call — never a change to this seam
// (CLAUDE.md §0.C: data-driven dispatch, zero compile-time branching).
public static class MapperRegistry
{
    private static readonly Dictionary<Type, IUnityObjectMapping> _map = new();

    public static Mapping<TSrc, TDst> Map<TSrc, TDst>(Func<ProcessedAssetCollection, TDst> create)
        where TSrc : UObject
        where TDst : IUnityObjectBase
    {
        Mapping<TSrc, TDst> mapping = new(create);
        _map[typeof(TSrc)] = mapping;
        return mapping;
    }

    // Convert one UE export, or null if no mapping covers its type. Dispatch is
    // exact-type-first then base-walk, so a single Map<UMaterialInterface,...>
    // covers every UMaterial / UMaterialInstanceConstant subclass without a
    // per-subclass registration (the doc's exact-GetType() lookup would miss
    // those — this base-walk is the correct generalization of its intent).
    public static IUnityObjectBase? Convert(UObject source, ProcessedAssetCollection collection)
    {
        for (Type? type = source.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            if (!_map.TryGetValue(type, out IUnityObjectMapping? mapping)) continue;
            try
            {
                return mapping.Apply(source, collection);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"mapping {type.Name} failed on UE asset '{source.Name}' ({source.GetType().Name}): {ex.Message}", ex);
            }
        }
        return null;
    }
}
