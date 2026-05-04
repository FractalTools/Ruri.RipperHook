using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects.Project;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

namespace Ruri.RipperHook.AR;

public partial class AR_SkipStreamingAssetsCopy_Hook
{
    [RetargetMethod(typeof(StreamingAssetsPostExporter), nameof(StreamingAssetsPostExporter.DoPostExport), isBefore: true, isReturn: true)]
    public void DoPostExport(GameData gameData, FullConfiguration settings, FileSystem fileSystem)
    {
        Logger.Info(LogCategory.Export, "Skipping Copying streaming assets...");
        return;
    }
}