using System;
using System.IO;
using System.Reflection;
using AssetRipper.SourceGenerated;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.RipperHook.AR;

public class EngineAssetsTpkHook : CommonHook, IHookModule
{
    public const string ResourceName = "EngineAssets.tpk";

    public void OnApply()
    {
    }

    [RetargetMethod(typeof(EngineAssetsTpk), nameof(GetStream))]
    public static Stream GetStream()
    {
        Assembly assembly = typeof(EngineAssetsTpkHook).Assembly;
        return assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException(
                $"[EngineAssetsTpkHook] {ResourceName} is not embedded in {assembly.GetName().Name}.");
    }
}
