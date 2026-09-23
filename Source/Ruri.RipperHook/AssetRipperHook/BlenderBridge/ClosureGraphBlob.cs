using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Processing;
using System.Text;

namespace Ruri.RipperHook.BlenderBridge;

/// <summary>One asset collection's identity inside a loaded closure: its bundle chain and its
/// own name, made unique when a closure pools two archives of one name. The same words on
/// every side that keys an asset, so a row read in one dataset names the asset another loads.</summary>
public static class ClosureGraphBlob
{
    public static Dictionary<AssetCollection, string> CollectionIdentities(GameData gameData)
    {
        Dictionary<AssetCollection, string> identities = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        foreach (AssetCollection collection in gameData.GameBundle.FetchAssetCollections())
        {
            StringBuilder builder = new(collection.Name.Length + 32);
            AppendBundleChain(collection.Bundle, builder);
            builder.Append(collection.Name);
            string identity = builder.ToString();
            occurrences.TryGetValue(identity, out int seen);
            occurrences[identity] = seen + 1;
            if (seen > 0)
            {
                identity = $"{identity}#{seen}";
            }
            identities.Add(collection, identity);
        }
        return identities;

        static void AppendBundleChain(Bundle? bundle, StringBuilder builder)
        {
            if (bundle is null || bundle.Parent is null)
            {
                return;
            }
            AppendBundleChain(bundle.Parent, builder);
            builder.Append(bundle.Name).Append('/');
        }
    }
}
