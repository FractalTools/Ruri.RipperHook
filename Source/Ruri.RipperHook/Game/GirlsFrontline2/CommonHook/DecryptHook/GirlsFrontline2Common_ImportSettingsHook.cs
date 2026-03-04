using AssetRipper.Import.Configuration;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.GirlsFrontline2;

public partial class GirlsFrontline2Common_Hook
{
    [RetargetMethod(typeof(ImportSettings), $"get_{nameof(ImportSettings.DefaultVersion)}")]
    [RetargetMethod(typeof(ImportSettings), $"get_{nameof(ImportSettings.TargetVersion)}")]
    public UnityVersion ImportSettings_get_DefaultVersion()
    {
        return new UnityVersion(2019, 4, 29, UnityVersionType.Final, 1);
    }
}