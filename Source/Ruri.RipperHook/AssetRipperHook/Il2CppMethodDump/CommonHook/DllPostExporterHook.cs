using System;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects.Scripts;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

namespace Ruri.RipperHook.AR;

public partial class AR_Il2CppMethodDump_Hook
{
    /// <summary>
    /// Prefix-continue hook on the dummy-DLL saver. Runs right before AssetRipper writes the Cpp2IL
    /// dummy assemblies, then lets the original proceed (isReturn:false).
    /// </summary>
    /// <remarks>
    /// 注意：这里的 <c>this</c> 是假的——注入的 IL 把 <see cref="DllPostExporter"/> 实例当作接收者传进来
    /// （与 AR_SkipStreamingAssetsCopy 同一套约定）。只能使用方法参数与静态状态，切勿访问本类实例成员。
    /// </remarks>
    [RetargetMethod(typeof(DllPostExporter), nameof(DllPostExporter.DoPostExport), isBefore: true, isReturn: false)]
    public void DoPostExport(GameData gameData, FullConfiguration settings, FileSystem fileSystem)
    {
        try
        {
            // 只对 IL2CPP 游戏生效；Mono 游戏的 AssemblyManager 不是 IL2CppManager，没有原生方法体可反汇编。
            if (gameData.AssemblyManager is not IL2CppManager)
            {
                return;
            }

            Il2CppMethodDumper.Dump(settings, fileSystem);
        }
        catch (Exception ex)
        {
            // 反汇编是附带产物，任何异常都不应影响正常的 dummy DLL 导出。
            Logger.Error(LogCategory.Export, $"[Il2CppMethodDump] {ex.GetType().Name}: {ex.Message}");
        }
    }
}
