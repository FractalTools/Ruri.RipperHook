using AssetRipper.Processing;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing.PrefabOutlining;

namespace Ruri.RipperHook.AR_PrefabOutlining;

public partial class AR_PrefabOutlining_Hook
{
    public static IEnumerable<IAssetProcessor> PrefabOutliningProcessor(FullConfiguration Settings)
    {
        yield return new PrefabOutliningProcessor();
    }
}
