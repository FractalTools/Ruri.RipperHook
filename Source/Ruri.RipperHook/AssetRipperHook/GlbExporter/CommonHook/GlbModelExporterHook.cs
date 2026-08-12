using AssetRipper.Assets;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Ruri.Hook.Attributes;
using SharpGLTF.Scenes;

namespace Ruri.RipperHook.GlbExporter;

public partial class AR_GlbExporter_Hook
{
    [RetargetMethod(typeof(GlbModelExporter), nameof(GlbModelExporter.ExportModel), isBefore: true, isReturn: true)]
    public static bool ExportModel(IEnumerable<IUnityObjectBase> assets, string path, bool isScene, FileSystem fileSystem)
    {
        try
        {
            SceneBuilder sceneBuilder = RuriGlbSceneBuilder.Build(assets, isScene);
            using Stream fileStream = fileSystem.File.Create(path);
            sceneBuilder.ToGltf2().WriteGLB(fileStream);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(LogCategory.Export, $"[GLB] full model export failed: {ex}");
            return false;
        }
    }
}
