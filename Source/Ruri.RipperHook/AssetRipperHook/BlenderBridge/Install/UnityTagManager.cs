using System.Collections.Concurrent;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_78;
using AssetRipper.SourceGenerated.Extensions;

namespace Ruri.RipperHook.BlenderBridge.Install;

/// <summary>
/// The names of the custom tags a Unity player ships in its own TagManager. An object carries its tag
/// as a number, and a custom tag (20000 and up) is named only by the TagManager in the build's
/// <c>globalgamemanagers</c>, which the closure a level is read through never reaches -- left to the
/// reader, such a tag comes out as "unknown_20000". Read off the install's <c>globalgamemanagers</c>
/// once per install.
/// </summary>
public static class UnityTagManager
{
    private const string EngineSettingsName = "globalgamemanagers";

    private static readonly ConcurrentDictionary<string, ITagManager> Managers = new(StringComparer.OrdinalIgnoreCase);

    public static string NameOf(string gameRoot, int tagId)
    {
        if (string.IsNullOrEmpty(gameRoot))
        {
            throw new InvalidOperationException(
                "a custom tag is named by its player's TagManager, and no install is open.");
        }
        return Managers.GetOrAdd(gameRoot, Read).TagIDToName(tagId);
    }

    private static ITagManager Read(string gameRoot)
    {
        PlayerIdentity player = InstallProbe.Project(gameRoot)
            ?? throw new InvalidOperationException($"no Unity player under '{gameRoot}' names its tags.");
        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
        GameData data = new ExportHandler(settings).Load([Path.Combine(player.DataFolder, EngineSettingsName)],
            LocalFileSystem.Instance);
        return data.GameBundle.FetchAssets().OfType<ITagManager>().Single();
    }
}
