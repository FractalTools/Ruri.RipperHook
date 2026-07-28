using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 不因末尾一个读不动的字段就丢弃整个 MonoBehaviour 的字段。
/// AssetRipper 原本一见 <c>ManagedReferencesRegistry</c> 就直接把 Structure 置 null,
/// 连普通字段一起丢;改为照常尝试读取,真读失败才置 null。
/// <para><b>这不等于支持 <c>[SerializeReference]</c></b>:注册表里每条 <c>ReferencedObjectData</c>
/// 的布局在类型树中为空(<c>SubNodes.Count is 0</c>),必须按 <c>{class, ns, asm}</c> 回程序集
/// 解析托管类型才能读。完整支持见 <c>Docs</c> 里的实现方案,尚未落地。</para>
/// </summary>
[RipperHook(GameType.AR_SerializeReference)]
public partial class AR_SerializeReference_Hook : RipperHookCommon
{
}
