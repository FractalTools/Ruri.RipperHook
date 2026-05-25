using Ruri.RipperHook.HookUtils.ExportHandlerHook;

namespace Ruri.RipperHook.AR_PrefabOutlining;

/// <summary>
/// 这个功能可能被移除 因此作为Hook留着.
/// 编译后的游戏通常不包含 prefab 的 link 信息, 这个是还原用的.
/// 旧版本作者写的形式 (基类 RipperHook + AddExtraHook) 在当前 Ruri.Hook 框架里不存在,
/// 改成跟 AR_StaticMeshSeparation_Hook 一样的 RipperHookCommon + RegisterModule 形式.
/// </summary>
[RipperHook(GameType.AR_PrefabOutlining)]
public partial class AR_PrefabOutlining_Hook : RipperHookCommon
{
    protected AR_PrefabOutlining_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RegisterModule(new ExportHandlerHook());
        ExportHandlerHook.CustomAssetProcessors.Add(PrefabOutliningProcessor);
        base.InitAttributeHook();
    }
}
