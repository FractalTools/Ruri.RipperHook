using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 静态网格分离 用于修复Static模型分离为单个网格
/// </summary>
[RipperHook(GameType.AR_StaticMeshSeparation)]
public partial class AR_StaticMeshSeparation_Hook : RipperHookCommon
{
    protected AR_StaticMeshSeparation_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.CustomAssetProcessors.Add(StaticMeshProcessor);
        base.InitAttributeHook();
    }
}
