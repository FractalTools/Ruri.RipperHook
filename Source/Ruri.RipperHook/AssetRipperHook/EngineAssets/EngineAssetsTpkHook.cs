using System;
using System.IO;
using System.Reflection;
using AssetRipper.SourceGenerated;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Serves Unity's built-in engine resources from a current <c>engine_assets.tpk</c> rather than the
/// one frozen inside the published <c>AssetRipper.SourceGenerated</c> package.
///
/// AssetRipper's code generator embeds whatever <c>engine_assets.tpk</c> it was handed as a byte
/// blob (<c>Pass557_CreateSourceTpkClass</c>), and both consumers -- the default-resource injector
/// (<c>GameInitializer.EngineResourceInjector</c>) and the engine assets exporter -- read it back
/// through <c>EngineAssetsTpk.GetStream()</c>. Those bytes are only as fresh as the last package
/// release, and that release predates the current tpk container format, so opening them throws.
///
/// The replacement is the artifact from the Tpk repository's own CI, which is precisely what
/// <c>AssetRipper.AssemblyDumper.Downloader</c> pulls when regenerating the package. Redirecting the
/// single accessor covers both consumers at once.
/// </summary>
public class EngineAssetsTpkHook : CommonHook, IHookModule
{
    public const string ResourceName = "EngineAssets.tpk";

    public void OnApply()
    {
        Registry.ApplyTypeHooks(GetType());
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
