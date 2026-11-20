using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

namespace Ruri.RipperHook.RuriEngine;

internal sealed class RuriEnginePostExporter(RuriEngineMapping mapping, GapReport gaps) : IPostExporter
{
    private const string MetaExtension = ".meta";

    public void DoPostExport(GameData gameData, FullConfiguration settings, FileSystem fileSystem)
    {
        string root = settings.ProjectRootPath;
        int folders = 0;
        int payloads = 0;
        foreach (UnityYamlAssetCollection collection in gameData.GameBundle.FetchAssetCollections().OfType<UnityYamlAssetCollection>())
        {
            ProjectEntry entry = collection.Entry;
            string target = fileSystem.Path.Join(root, entry.RelativePath);
            switch (entry.Kind)
            {
                case ProjectFileKind.Folder:
                    fileSystem.Directory.Create(target);
                    WriteMeta(entry, target, fileSystem);
                    folders++;
                    break;
                case ProjectFileKind.Payload:
                    fileSystem.Directory.Create(fileSystem.Path.GetDirectoryName(target));
                    using (Stream source = fileSystem.File.OpenRead(entry.AbsolutePath))
                    using (Stream destination = fileSystem.File.Create(target))
                    {
                        source.CopyTo(destination);
                    }
                    WriteMeta(entry, target, fileSystem);
                    payloads++;
                    break;
            }
        }
        Logger.Info(LogCategory.Export, $"[RuriEngine] wrote {folders} folder meta file(s) and copied {payloads} payload file(s)");

        string reportPath = RuriEngineSession.GapReportPath ?? fileSystem.Path.Join(settings.ExportRootPath, GapReport.FileName);
        gaps.Write(fileSystem, reportPath);
        Logger.Info(LogCategory.Export, $"[RuriEngine] gap report written to {reportPath}");
    }

    private void WriteMeta(ProjectEntry entry, string target, FileSystem fileSystem)
    {
        fileSystem.File.WriteAllBytes(target + MetaExtension, MetaJson.Write(entry.Meta, mapping));
    }
}
