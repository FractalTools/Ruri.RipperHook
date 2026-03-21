using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Logging;
using AssetRipper.Primitives;
using AssetRipper.Processing;
using Ruri.RipperHook.Crypto;
using Ruri.RipperHook.HookUtils.BundleFileBlockReaderHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleFileHook;
using Ruri.RipperHook.HookUtils.FileStreamBundleHeaderHook;
using Ruri.RipperHook.HookUtils.GameBundleHook;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_CollectAssetBundles;
using Ruri.RipperHook.HookUtils.PlatformGameStructureHook_IsBundleHeader;
using MonoModHook = MonoMod.RuntimeDetour.Hook;

namespace Ruri.RipperHook.Endfield;

[GameHook("EndField", "1.1.9", "2021.3.34f1")]
public partial class EndField_1_1_9_Hook : EndFieldCommon_Hook
{
    public static UnityVersion endFieldClassVersion = new UnityVersion(2021, 3, 119, UnityVersionType.Experimental, (byte)CustomEngineType.EndField);
    private static Endfield_1_1_9_VFSDecryptor vfsDecryptor;
    private static MonoModHook? _processHook;

    protected EndField_1_1_9_Hook()
    {
        vfsDecryptor = new Endfield_1_1_9_VFSDecryptor();
    }

    protected override UnityVersion GetTargetVersion(GameHookAttribute attr)
    {
        return endFieldClassVersion;
    }

    protected override void InitAttributeHook()
    {
        ApplyUSCSandboxFixes();

        RegisterModule(new FileStreamBundleHeaderHook(CustomReadHeader));
        RegisterModule(new FileStreamBundleFileHook(CustomReadFileStreamMetadata));
        RegisterModule(new GameBundleHook(CustomFilePreInitialize));
        RegisterModule(new PlatformGameStructureHook_CollectAssetBundles(EndField_0_5_27_Hook.CustomAssetBundlesCheck));
        RegisterModule(new PlatformGameStructureHook_IsBundleHeader(CustomAssetBundlesCheckMagicNum));
        RegisterModule(new BundleFileBlockReaderHook(CustomBlockCompression));
        RegisterAnimatorControllerHook();
        HookExportHandlerProcess();

        Endfield_1_1_9_GpuType33Transform.IsEnabled = true;

        base.InitAttributeHook();
    }

    /// <summary>
    /// Hook ExportHandler.Process to catch individual processor failures.
    /// Endfield's custom engine causes AssetRipper processors (AnimationClip, etc.)
    /// to fail on certain assets. This hook ensures each processor failure is
    /// logged but doesn't stop the entire pipeline.
    /// </summary>
    private void HookExportHandlerProcess()
    {
        var method = typeof(ExportHandler).GetMethod("Process", new[] { typeof(GameData) });
        if (method == null)
        {
            Console.WriteLine("    [!] ExportHandler.Process not found — processor error tolerance unavailable");
            return;
        }

        var getProcessors = typeof(ExportHandler).GetMethod("GetProcessors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (getProcessors == null)
        {
            Console.WriteLine("    [!] ExportHandler.GetProcessors not found — processor error tolerance unavailable");
            return;
        }

        _processHook = new MonoModHook(method,
            (Action<Action<ExportHandler, GameData>, ExportHandler, GameData>)
            ((orig, self, gameData) =>
            {
                Logger.Info(LogCategory.Processing, "Processing loaded assets...");
                foreach (IAssetProcessor processor in (IEnumerable<IAssetProcessor>)getProcessors.Invoke(self, null)!)
                {
                    try
                    {
                        processor.Process(gameData);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(LogCategory.Processing, $"{processor.GetType().Name} failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Logger.Info(LogCategory.Processing, "Finished processing assets");
            })
        );
        Console.WriteLine("    [+] Hooked ExportHandler.Process (per-processor error tolerance)");
    }
}
