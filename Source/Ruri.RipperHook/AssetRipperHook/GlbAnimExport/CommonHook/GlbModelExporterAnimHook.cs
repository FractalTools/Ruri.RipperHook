using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Ruri.Hook.Attributes;
using SharpGLTF.Scenes;

namespace Ruri.RipperHook.GlbAnimExport;

public partial class AR_GlbAnimExport_Hook
{
    /// <summary>
    /// 替换 GlbModelExporter.ExportModel:用带骨架/蒙皮/动画的 RuriGlbModelBuilder 构建场景再写 GLB。
    /// 失败时记日志并返回 false(与原实现的失败语义一致)。
    /// </summary>
    [RetargetMethod(typeof(GlbModelExporter), nameof(GlbModelExporter.ExportModel), isBefore: true, isReturn: true)]
    public static bool ExportModel(IEnumerable<IUnityObjectBase> assets, string path, bool isScene, FileSystem fileSystem)
    {
        try
        {
            SceneBuilder sceneBuilder = RuriGlbModelBuilder.Build(assets, isScene);
            using Stream fileStream = fileSystem.File.Create(path);
            sceneBuilder.ToGltf2().WriteGLB(fileStream);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(LogCategory.Export, $"[GLB] animated export failed: {ex}");
            return false;
        }
    }
}
