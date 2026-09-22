using AssetRipper.Export.Configuration;
using AssetRipper.Processing;

namespace Ruri.RipperHook.AR;

public partial class AR_StaticMeshSeparation_Hook
{
    public static IEnumerable<IAssetProcessor> StaticMeshProcessor(FullConfiguration Settings)
    {
        if (Settings.ProcessingSettings.EnableStaticMeshSeparation)
        {
            yield return new StaticMeshProcessor();
        }
    }
}