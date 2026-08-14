using System;
using System.Collections.Generic;
using System.Reflection;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Export.UnityProjects.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;

using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.ExportHandlerHook;

public class ExportHandlerHook : CommonHook, IHookModule
{
    public void OnApply()
    {
        Registry.ApplyTypeHooks(GetType());
    }

    public delegate IEnumerable<IAssetProcessor> AssetProcessorDelegate(FullConfiguration Settings);

    public static List<AssetProcessorDelegate> CustomAssetProcessors = new List<AssetProcessorDelegate>();

    private static readonly PropertyInfo SettingsProperty =
        typeof(ExportHandler).GetProperty("Settings",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMemberException(nameof(ExportHandler), "Settings");

    private static readonly MethodInfo UpstreamGetProcessors =
        typeof(ExportHandler).GetMethod("GetProcessors",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new MissingMemberException(nameof(ExportHandler), "GetProcessors");

    [RetargetMethod(typeof(ExportHandler), nameof(Process))]
    private void Process(GameData gameData)
    {
        Logger.Info(LogCategory.Processing, "Processing loaded assets...");
        foreach (IAssetProcessor processor in GetProcessors())
        {
            processor.Process(gameData);
        }
        Logger.Info(LogCategory.Processing, "Finished processing assets");
    }

    private IEnumerable<IAssetProcessor> GetProcessors()
    {
        List<IAssetProcessor> processors =
            new((IEnumerable<IAssetProcessor>)UpstreamGetProcessors.Invoke(this, null)!);
        if (CustomAssetProcessors.Count == 0)
        {
            return processors;
        }

        FullConfiguration settings = (FullConfiguration)SettingsProperty.GetValue(this)!;
        List<IAssetProcessor> custom = new();
        foreach (AssetProcessorDelegate factory in CustomAssetProcessors)
        {
            custom.AddRange(factory(settings));
        }

        int anchor = processors.FindIndex(static processor => processor is LightingDataProcessor);
        if (anchor < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(ExportHandler)}.GetProcessors no longer yields {nameof(LightingDataProcessor)}, "
                + $"so the {nameof(CustomAssetProcessors)} insertion point is gone. Re-anchor "
                + $"{nameof(ExportHandlerHook)} against the current upstream pipeline.");
        }
        processors.InsertRange(anchor, custom);
        return processors;
    }
}
