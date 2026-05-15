namespace Ruri.RipperHook.AR;

/// <summary>
/// 最小差分 hook 让 AR 的 AnimationClip 导出在数据层面对齐 VibeStudio (AssetStudio):
///
///   * <c>AnimationClipConverter.GetReversedPath</c> 改成 VibeStudio 风格的
///     <c>"&lt;prefix&gt;&lt;decimal-hash&gt;"</c>(原本是 <c>"&lt;prefix&gt;0x&lt;HEX&gt;_&lt;6 ascii&gt;"</c>),
///     一次替换覆盖 path / missed / script / typetree 四种前缀.
///   * <c>CustomCurveResolver.ToAttributeName</c> 把 RendererMaterial / BlendShape
///     未解析时的 fallback 从 "&lt;prefix&gt;.&lt;path&gt;" 改成
///     "&lt;prefix&gt;.&lt;decimal-attribute&gt;"。AR 原版用 path 做 fallback,
///     同一未解析 path 上挂多个 material 属性时会发生 CurveData 冲突, 后挂的
///     FloatCurve 被静默丢掉; VibeStudio 用属性 hash 做 key, 不冲突.
///
/// 不替换 <c>AnimationClipConverter.Process</c> 本身。AR 的 streamed/dense/constant
/// 流水线已经是正确的——前提是各游戏的特殊曲线编码 (Mihoyo ACL, Endfield AKEF,
/// ZZZ ACL2, LnD …) 已经在游戏自己的反序列化 hook 里被 *回填* 进标准
/// <c>DenseClip.m_SampleArray</c> (AKEF 走
/// <see cref="Ruri.RipperHook.Endfield.EndFieldCommon_Hook"/> 的 DenseClip
/// post-process). 走到本 hook 时数据已是 Unity 原生形态.
/// </summary>
[RipperHook(GameType.AR_AnimationExport)]
public partial class AR_AnimationExport_Hook : RipperHookCommon
{
}
