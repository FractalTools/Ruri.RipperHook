using System.Collections.Concurrent;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated.Classes.ClassID_30;

namespace Ruri.RipperHook.Core.Install;

/// <summary>
/// The lighting switch a Unity player ships in its own GraphicsSettings: whether lights take
/// their intensity in linear space. UnityPlayer's <c>Light::UpdateFinalColor</c> branches on it,
/// so what a light emits is not knowable without it. Read off the install's
/// <c>globalgamemanagers</c> once per install.
/// </summary>
public static class UnityGraphicsSettings
{
    private const string EngineSettingsName = "globalgamemanagers";

    private static readonly ConcurrentDictionary<string, bool> LinearIntensity = new(StringComparer.OrdinalIgnoreCase);

    public static bool LightsUseLinearIntensity(string gameRoot)
    {
        if (string.IsNullOrEmpty(gameRoot))
        {
            throw new InvalidOperationException(
                "what a Unity light emits depends on its player's GraphicsSettings, and no install is open.");
        }
        return LinearIntensity.GetOrAdd(gameRoot, Read);
    }

    private static bool Read(string gameRoot)
    {
        PlayerIdentity player = InstallProbe.Project(gameRoot)
            ?? throw new InvalidOperationException($"no Unity player under '{gameRoot}' states its GraphicsSettings.");
        FullConfiguration settings = new();
        settings.LoadFromDefaultPath();
        settings.ImportSettings.ScriptContentLevel = ScriptContentLevel.Level0;
        GameData data = new ExportHandler(settings).Load([Path.Combine(player.DataFolder, EngineSettingsName)],
            LocalFileSystem.Instance);
        IGraphicsSettings graphics = data.GameBundle.FetchAssets().OfType<IGraphicsSettings>().Single();
        if (!graphics.Has_LightsUseLinearIntensity())
        {
            throw new NotSupportedException(
                $"the player under '{gameRoot}' predates the linear light intensity switch; its light colour rule is unread.");
        }
        return graphics.LightsUseLinearIntensity;
    }
}
