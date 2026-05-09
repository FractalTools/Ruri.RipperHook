using AssetRipper.Processing.AnimationClips;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using Ruri.RipperHook.AnimationExport;

namespace Ruri.RipperHook.AR;

public partial class AR_AnimationExport_Hook
{
    /// <summary>
    /// 完全替换 <see cref="AnimationClipConverter.Process"/>.
    /// <see cref="VibeStudioAnimationProcessor"/> 是 AssetStudio.ModelConverter.ConvertAnimations +
    /// ReadCurveData 的等价移植, 写入目标改为 AR 的曲线列表 (PositionCurves_C74 等), AR 后续
    /// .anim YAML 导出走原生路径, 输出格式不变.
    /// 本 hook 不感知具体游戏 —— 假定数据已经被各游戏自己的反序列化 hook 回填成标准 Unity 形态.
    /// </summary>
    [RetargetMethod(typeof(AnimationClipConverter), nameof(AnimationClipConverter.Process), isBefore: true, isReturn: true)]
    public static void Process(IAnimationClip clip, PathChecksumCache checksumCache)
    {
        VibeStudioAnimationProcessor.Process(clip, checksumCache);
    }
}
