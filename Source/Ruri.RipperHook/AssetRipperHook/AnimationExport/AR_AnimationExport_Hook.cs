namespace Ruri.RipperHook.AR;

/// <summary>
/// 用 VibeStudio (AssetStudio) 等价的算法接管 AssetRipper 的 AnimationClip 处理.
/// 在保留 AR 的 .anim YAML 输出格式不变的前提下:
///   - 解码 m_CompressedRotationCurves (AR 默认丢弃, 且 AR 自带 Unpack 把时间字段当绝对值, 与 Unity 真实编码不符).
/// 本 hook 不感知具体游戏. 各游戏的特殊曲线编码 (Mihoyo ACL, Endfield AKEF, ZZZ ACL2, LnD 等)
/// 应当在游戏自己的反序列化 hook 里完成 *回填* —— 像 ExAstrisCommon_Hook.DenseClip_ReadRelease
/// 把 ACL 字节合并进 m_SampleArray, 或像 Shader 解码 hook 把字节回填进 m_CompressedBlob 那样.
/// 把游戏专属编码翻译成标准 Unity 形态后, 本 hook 看到的就是普通 StreamedClip / DenseClip /
/// ConstantClip / CompressedRotationCurves, 走原生路径就能正确导出.
/// </summary>
[RipperHook(GameType.AR_AnimationExport)]
public partial class AR_AnimationExport_Hook : RipperHookCommon
{
}
