using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;
namespace Ruri.RipperHook.AR;

/// <summary>
/// 跳过StreamingAssets的复制
/// </summary>
[RipperHook(GameType.AR_SkipStreamingAssetsCopy)]
public partial class AR_SkipStreamingAssetsCopy_Hook : RipperHookCommon
{
}
