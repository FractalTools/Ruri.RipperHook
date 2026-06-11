using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.GlbAnimExport;

/// <summary>
/// 给 AR 的 GLB 模型导出补上蒙皮 + 动画(AR 原生 GLB 只导静态刚体网格,既无骨架也无动画)。
/// 替换 GlbModelExporter.ExportModel,改用 <see cref="RuriGlbModelBuilder"/> 构建带骨架/蒙皮/动画的场景。
/// 替换实现见 CommonHook\GlbModelExporterAnimHook。
/// </summary>
[RipperHook(GameType.AR_GlbAnimExport)]
public partial class AR_GlbAnimExport_Hook : RipperHookCommon
{
}
