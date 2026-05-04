using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.FileProvider.Objects;
using FModel;
using FModel.Services;
using Ruri.FModelHook.Attributes;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.AutoExport
{
    // Auto-driver hook. Boots FModel as usual (so the user's
    // UserSettings / encryption keys / mappings / Oodle / Zlib all
    // initialise the same way they do for an interactive session)
    // then, after the GUI is up and the provider has mounted, walks
    // every relevant package and drives FModel's own ExportData
    // pipeline programmatically.
    //
    // The interactive UE_ShaderDecompiler hook (which writes
    // .ushaderlib + sidecars + UnifiedShaderMetadata.json from
    // ExportData) fires unchanged when this driver triggers
    // ExportData under the hood. Nothing else needs to change.
    //
    // This hook owns its own CLI parsing — Program.cs forwards
    // args via the standard process command line, the hook reads
    // them in Initialize(). To activate, run:
    //
    //   Ruri.FModelHook.exe --auto-export-cook
    //                       [--shader-only]
    //                       [--no-quit]
    //                       [--ready-timeout-sec <int>]
    //
    // Game directory / AES keys / mappings come from the user's
    // current FModel UserSettings (the same place the GUI reads),
    // matching the manual workflow the user already validated.
    [FModelHook(GameType.UE_ShaderDecompiler_AutoExport)]
    public class UE_ShaderDecompiler_AutoExport_Hook : RuriHook
    {
        private static volatile bool _autoExportRequested;
        private static volatile bool _shaderOnly;
        private static volatile bool _quitWhenDone = true;
        private static int _readyTimeoutSec = 180;
        private static int _runOnceGuard;

        public override void Initialize()
        {
            ParseCliArgs(Environment.GetCommandLineArgs());
            base.Initialize();

            if (_autoExportRequested)
            {
                HookLogger.LogSuccess($"[AutoExport] Hook armed. shader-only={_shaderOnly} quit-when-done={_quitWhenDone} ready-timeout={_readyTimeoutSec}s");
            }
        }

        // Detour FModel's MainWindow.OnLoaded at entry. The original method
        // is async; awaits don't complete by the time we return, so we
        // spawn a polling task that watches Provider.Files for readiness
        // before calling vm.ExportData (which fires the existing
        // UE_ShaderDecompiler hook).
        [RetargetMethod(typeof(MainWindow), "OnLoaded", true, false)]
        public static void OnLoaded_Before(MainWindow self, object sender, RoutedEventArgs e)
        {
            if (!_autoExportRequested) return;
            if (System.Threading.Interlocked.Exchange(ref _runOnceGuard, 1) == 1) return;

            HookLogger.Log("[AutoExport] MainWindow loading — polling for provider readiness...");
            _ = Task.Run(DriveAutoExportAsync);
        }

        private static async Task DriveAutoExportAsync()
        {
            try
            {
                var vm = await WaitForProviderReady();
                if (vm == null)
                {
                    HookLogger.LogFailure("[AutoExport] Provider did not become ready before timeout. Aborting.");
                    return;
                }

                HookLogger.LogSuccess($"[AutoExport] Provider ready. ProjectName={vm.Provider.ProjectName} Files={vm.Provider.Files.Count} VFS={vm.Provider.MountedVfs.Count}");

                int shaderCount = await ExportEntries(vm, IsShaderBytecode, "shader bytecode");
                int materialCount = 0;
                if (!_shaderOnly)
                {
                    materialCount = await ExportEntries(vm, IsMaterialAsset, "material");
                }

                HookLogger.LogSuccess($"[AutoExport] Done. shaders={shaderCount} materials={materialCount}");
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[AutoExport] Driver crashed: {ex}");
            }
            finally
            {
                if (_quitWhenDone)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try { Application.Current.Shutdown(); } catch { }
                    });
                }
            }
        }

        private static async Task<FModel.ViewModels.CUE4ParseViewModel?> WaitForProviderReady()
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < _readyTimeoutSec)
            {
                var vm = ApplicationService.ApplicationView?.CUE4Parse;
                if (vm?.Provider != null
                    && vm.Provider.Files.Count > 0
                    && vm.Provider.MountedVfs.Count > 0)
                {
                    // Quick stability check — wait for the file count to settle
                    // (FModel may still be mounting additional containers).
                    int previous = vm.Provider.Files.Count;
                    await Task.Delay(750);
                    if (vm.Provider.Files.Count == previous)
                    {
                        return vm;
                    }
                }
                await Task.Delay(500);
            }
            return null;
        }

        private static async Task<int> ExportEntries(
            FModel.ViewModels.CUE4ParseViewModel vm,
            Func<GameFile, bool> selector,
            string label)
        {
            var entries = vm.Provider.Files.Values.Where(selector).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            HookLogger.Log($"[AutoExport] Exporting {entries.Count} {label} entries...");
            int count = 0;
            foreach (var entry in entries)
            {
                try
                {
                    // ExportData must run on the UI thread for FModel's
                    // logger / threadworker to behave as if the user
                    // clicked Export. Marshal back to dispatcher.
                    await Application.Current.Dispatcher.InvokeAsync(() => vm.ExportData(entry, false));
                    count++;
                    if (count % 25 == 0)
                    {
                        HookLogger.Log($"[AutoExport] {label} progress: {count}/{entries.Count}");
                    }
                }
                catch (Exception ex)
                {
                    HookLogger.LogFailure($"[AutoExport] {label} {entry.Path}: {ex.Message}");
                }
            }
            return count;
        }

        private static bool IsShaderBytecode(GameFile file) =>
            file.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase);

        private static bool IsMaterialAsset(GameFile file)
        {
            if (!file.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase)) return false;
            if (file.Name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Name.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Name.StartsWith("MPC_", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.Path.Contains("/Material", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void ParseCliArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--auto-export-cook", StringComparison.OrdinalIgnoreCase))
                {
                    _autoExportRequested = true;
                }
                else if (string.Equals(a, "--shader-only", StringComparison.OrdinalIgnoreCase))
                {
                    _shaderOnly = true;
                }
                else if (string.Equals(a, "--no-quit", StringComparison.OrdinalIgnoreCase))
                {
                    _quitWhenDone = false;
                }
                else if (string.Equals(a, "--ready-timeout-sec", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out int v))
                {
                    _readyTimeoutSec = Math.Max(10, v);
                    i++;
                }
            }
        }
    }
}
