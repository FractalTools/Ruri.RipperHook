using AssetRipper.Processing;
using AssetRipper.Export.Configuration;
using AssetRipper.Processing.PrefabOutlining;

namespace Ruri.RipperHook.AR_PrefabOutlining;

public partial class AR_PrefabOutlining_Hook
{
    // Signature follows Ruri.RipperHook.HookUtils.ExportHandlerHook.AssetProcessorDelegate (FullConfiguration).
    // 启用 AR_PrefabOutlining hook 即代表要跑这个 processor —— 不再额外读
    // Settings.ProcessingSettings.EnablePrefabOutlining (那个开关是 AR 老版本走 GUI 设置的入口,
    // 在我们 hook 走 Ruri.Hook config 的体系下重复了).
    public static IEnumerable<IAssetProcessor> PrefabOutliningProcessor(FullConfiguration Settings)
    {
        yield return new PrefabOutliningProcessor();
    }
}
