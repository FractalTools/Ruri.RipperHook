using AssetRipper.Assets;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;

namespace Ruri.RipperHook.CabMapping;

internal sealed class ReferenceWalker : AssetWalker
{
    private int _depth;
    private string _outermost = string.Empty;

    public List<(string Field, int FileId, long PathId)> Found { get; } = new();

    public void Clear()
    {
        Found.Clear();
        _depth = 0;
        _outermost = string.Empty;
    }

    public override bool EnterField(IUnityAssetBase asset, string name)
    {
        if (_depth++ == 0)
        {
            _outermost = name;
        }
        return true;
    }

    public override void ExitField(IUnityAssetBase asset, string name)
    {
        _depth--;
    }

    public override void VisitPPtr<TAsset>(PPtr<TAsset> pptr)
    {
        if (!pptr.IsNull)
        {
            Found.Add((_outermost, pptr.FileID, pptr.PathID));
        }
    }
}
