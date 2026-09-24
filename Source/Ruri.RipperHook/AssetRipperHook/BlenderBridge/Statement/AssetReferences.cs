using AssetRipper.Assets;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>
/// The assets a set of scripted-asset texts points at without carrying, under the pointer each text writes:
/// <c>{fileID: the asset's path id, guid: the MD5 of its archive's name}</c>.
/// </summary>
public sealed class AssetReferences
{
    private readonly Dictionary<(string Guid, long FileId), IUnityObjectBase> _assets = [];

    internal UnityGuid Add(IUnityObjectBase asset)
    {
        UnityGuid guid = UnityGuid.Md5Hash(asset.Collection.Name);
        _assets[(guid.ToString(), asset.PathID)] = asset;
        return guid;
    }

    /// <summary>The asset a text's pointer names, or null for a pointer no text of this read wrote.</summary>
    public IUnityObjectBase? Resolve(string guid, long fileId) =>
        _assets.TryGetValue((guid, fileId), out IUnityObjectBase? asset) ? asset : null;
}
