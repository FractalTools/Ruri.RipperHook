using Ruri.RipperHook.Attributes;

namespace Ruri.RipperHook.GlbExporter;

/// <summary>
/// 完整模型 GLB 导出:给 AR 的 GLB 模型导出补上蒙皮、材质贴图、morph target 与动画
/// (AR 原生 GLB 只导静态刚体网格,无骨架/无动画/无 morph)。入口 = 选中的 prefab 或 Animator:
/// 曲线路径已由 AR 的 PathChecksumCache(Avatar TOS + 层级 CRC32)在处理阶段还原;humanoid 肌肉曲线
/// 由 <see cref="HumanoidClipBaker"/> 按 Avatar 肌肉参照系烘焙成骨骼旋转轨道。单独选一个 anim
/// 不可用——脱离 prefab/Avatar 无法还原 path_hash。
/// 替换实现见 CommonHook\GlbModelExporterHook。
/// </summary>
[RipperHook(GameType.AR_GlbExporter)]
public partial class AR_GlbExporter_Hook : RipperHookCommon
{
}
