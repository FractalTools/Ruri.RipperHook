using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using Ruri.RipperHook.Core.Capabilities;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.FModelHook.Unreal;

[GameCapabilities(Ruri.RipperHook.GameType.UnrealEngine)]
public static class UnrealEngine_Capabilities
{
    /// <summary>
    /// AssetRipper's file loader, on an Unreal install: there is nothing to hand it.
    ///
    /// An Unreal build carries no Unity file to parse, and this decoder no longer manufactures
    /// one -- what its packages hold is published as data (see <see cref="UnrealDatasets"/>) for
    /// a host to build from directly. So the bundle stays empty, and the one thing worth doing
    /// here is SAYING so: a run that asked for a Unity-project export would otherwise write an
    /// empty project and report success, which is the failure this codebase treats as the worst
    /// kind -- a result that looks complete.
    /// </summary>
    [Since("4.0")]
    [FeedsModule(typeof(GameBundleHook), nameof(GameBundleHook.CustomFilePreInitialize))]
    public static void GameBundlePreInitialize(GameBundle _this, IEnumerable<string> paths, List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider)
    {
        Logger.Info(LogCategory.Import,
            "[Unreal] This build is read as data, not as Unity assets: nothing is loaded into the "
            + "asset bundle and a project export would be empty. Its packages are published as "
            + "datasets (unreal.placements, unreal.mesh.geometry, unreal.mesh.skeleton, "
            + "unreal.materials, unreal.textures, unreal.animations) for a host to build from.");
    }
}
