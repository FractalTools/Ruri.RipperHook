using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.SerializedFiles.Parser;

namespace Ruri.RipperHook.HookUtils.GameBundleHook;

public sealed class ClassFilterAssetFactory : AssetFactoryBase
{
    private readonly AssetFactoryBase _inner;
    private readonly HashSet<int> _classIds;

    public ClassFilterAssetFactory(AssetFactoryBase inner, params int[] classIds)
    {
        _inner = inner;
        _classIds = [.. classIds];
    }

    public override IUnityObjectBase? ReadAsset(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType, IReadOnlyList<SerializedTypeReference> referenceTypes)
    {
        return _classIds.Contains(assetInfo.ClassID)
            ? _inner.ReadAsset(assetInfo, assetData, assetType, referenceTypes)
            : null;
    }
}
