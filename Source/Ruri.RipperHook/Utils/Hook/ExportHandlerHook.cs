using System;
using System.Collections.Generic;
using System.Reflection;
using AssetRipper.Assets.Bundles;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects; // ExportHandler所在命名空间
using AssetRipper.Export.UnityProjects.Configuration; // 如果FullConfiguration在这个空间，如果不是请检查AssetRipper.Export.Configuration
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Processing;
using AssetRipper.Processing.AnimatorControllers;
using AssetRipper.Processing.Assemblies;
using AssetRipper.Processing.AudioMixers;
using AssetRipper.Processing.Editor;
using AssetRipper.Processing.Prefabs;
using AssetRipper.Processing.Scenes;
using AssetRipper.Processing.ScriptableObject;
using AssetRipper.Processing.Textures;

using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.ExportHandlerHook;

public class ExportHandlerHook : CommonHook, IHookModule
{
    public void OnApply()
    {
        Registry.ApplyTypeHooks(GetType());
    }
    // update: 配置类类型从 LibraryConfiguration 变更为 FullConfiguration
    public delegate IEnumerable<IAssetProcessor> AssetProcessorDelegate(FullConfiguration Settings);

    public static List<AssetProcessorDelegate> CustomAssetProcessors = new List<AssetProcessorDelegate>();

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
        // update: 获取 Settings 属性 (类型变更为 FullConfiguration)
        // 这里的 BindingFlags 视你的 CommonHook 环境而定，通常 Protected 需要 NonPublic | Instance
        var settingsProp = typeof(ExportHandler).GetProperty("Settings", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var Settings = (FullConfiguration)settingsProp.GetValue(this);

        // --- 以下完全复制新版 ExportHandler.GetProcessors 的逻辑 ---

        // Assembly processors (新版本新增的大量预处理)
        yield return new AttributePolyfillGenerator();
        yield return new MonoExplicitPropertyRepairProcessor();
        yield return new ObfuscationRepairProcessor();
        yield return new ForwardingAssemblyGenerator();

        if (Settings.ImportSettings.ScriptContentLevel == ScriptContentLevel.Level1)
        {
            yield return new MethodStubbingProcessor();
        }

        yield return new NullRefReturnProcessor(Settings.ImportSettings.ScriptContentLevel);
        yield return new UnmanagedConstraintRecoveryProcessor();

        if (Settings.ProcessingSettings.RemoveNullableAttributes)
        {
            yield return new NullableRemovalProcessor();
        }
        if (Settings.ProcessingSettings.PublicizeAssemblies)
        {
            yield return new SafeAssemblyPublicizingProcessor();
        }

        yield return new RemoveAssemblyKeyFileAttributeProcessor();
        yield return new InternalsVisibileToPublicKeyRemover();

        // Standard Asset Processors
        yield return new SceneDefinitionProcessor();
        yield return new MainAssetProcessor();
        yield return new AnimatorControllerProcessor();
        yield return new AudioMixerProcessor();
        yield return new EditorFormatProcessor(Settings.ProcessingSettings.BundledAssetsExportMode);

        // --- Hook 插入点 ---
        // 保持在你原来的位置：在 EditorFormatProcessor 之后，LightingDataProcessor 之前
        foreach (var CustomAssetProcessor in CustomAssetProcessors)
        {
            foreach (var processor in CustomAssetProcessor(Settings))
            {
                yield return processor;
            }
        }
        // ------------------

        //Static mesh separation goes here
        yield return new LightingDataProcessor(); //Needs to be after static mesh separation
        yield return new PrefabProcessor();
        yield return new SpriteProcessor();
        yield return new ScriptableObjectProcessor(); // update: 新版本新增了 ScriptableObject 处理器
    }
}