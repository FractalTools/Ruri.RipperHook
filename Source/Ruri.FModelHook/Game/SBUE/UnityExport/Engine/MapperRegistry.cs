using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using CUE4Parse.UE4.Assets.Exports;

namespace Ruri.FModelHook.Game.SBUE.UnityExport.Engine;

// The central UE-type -> Unity-mapping table. Mappings register here once at
// startup; ConversionContext looks one up per UE object. Adding an asset family
// is one Map<,>() call — never a change to this seam (CLAUDE.md §0.C:
// data-driven dispatch, zero compile-time branching).
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

    // Find the mapping for a UE export type: exact-type-first, then base-walk, so
    // a single Map<UMaterialInterface,...> covers every UMaterial /
    // UMaterialInstanceConstant subclass without a per-subclass registration.
    public static IUnityObjectMapping? Find(Type sourceType)
    {
        for (Type? type = sourceType; type != null && type != typeof(object); type = type.BaseType)
            if (_map.TryGetValue(type, out IUnityObjectMapping? mapping)) return mapping;
        return null;
    }
}
