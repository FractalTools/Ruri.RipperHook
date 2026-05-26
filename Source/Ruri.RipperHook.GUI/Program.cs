using System;
using System.IO;
using System.Windows.Forms;
using AssetRipper.Import.Logging;
using Ruri.Hook.Config;
using Ruri.RipperHook;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.GUI;

internal static class Program
{
    // Single unified host config: enabled-hook list + per-module settings
    // bag in one JSON.
    private const string ConfigFileName = "RuriRipperHook.json";

    [STAThread]
    public static int Main(string[] args)
    {
        Bootstrap.InstallAssemblyResolver();

        // AssetRipper 的 Logger 是个全局 static 类, 不挂 sink 的话 Logger.Info/Warning/Error 全部直接黑洞.
        // CLI 在 HeadlessRunner 里挂了 StderrLogger+FileLogger, GUI 之前漏了, 导致加载文件时控制台一片空白 ——
        // 只有用 Console.WriteLine 直接写的 hook 提示能透出来. 这里挂上 ConsoleLogger 就把 import/process/export
        // 所有 Logger.Info(...) 都打到 Exe 自带的 console.
        Logger.Add(new ConsoleLogger());

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        bool isFirstRun = !File.Exists(configPath);
        var config = HookConfig.Load(configPath);

        if (isFirstRun)
        {
            // First-launch defaults for AR_* feature toggles. SkipStreamingAssetsCopy is on so the
            // exporter doesn't duplicate the original StreamingAssets tree next to converted output.
            // After the first Settings save the file exists and we respect whatever the user picked.
            config.EnabledHooks.Add("AR_SkipStreamingAssetsCopy_");
            config.Save(configPath);
        }

        // Module settings load BEFORE hooks fire so any hook-side static
        // accessor (ShaderDecompilerSettingsAccess.Current) sees the
        // persisted value at first read.
        WireModuleSettings(config, configPath);

        Bootstrap.ApplyHooks(config);

		Application.Run(new MainForm(config, configPath));
        return 0;
    }

    private static void WireModuleSettings(HookConfig config, string configPath)
    {
        ShaderDecompilerSettings shader = config.GetModuleSettings<ShaderDecompilerSettings>(ShaderDecompilerSettings.ModuleKey) ?? new ShaderDecompilerSettings();
        ShaderDecompilerSettingsAccess.Replace(shader);
        ShaderDecompilerSettingsAccess.RegisterSaver(updated =>
        {
            // Re-read + re-write so concurrent edits to OTHER modules
            // (a future settings UI for a different hook) are preserved.
            HookConfig live = HookConfig.Load(configPath);
            live.SetModuleSettings(ShaderDecompilerSettings.ModuleKey, updated);
            live.Save(configPath);
        });

        // AR native settings live in our unified JSON (RuriRipperHook.json) instead of AR's own
        // SerializedSettings file. Apply the persisted snapshot onto GameFileLoader.Settings on
        // startup so user choices survive across sessions without depending on AR's
        // ExportSettings.SaveSettingsToDisk flag.
        SettingsDialog.ArSettingsSnapshot? snapshot = config.GetModuleSettings<SettingsDialog.ArSettingsSnapshot>(SettingsDialog.ArSettingsModuleKey);
        snapshot?.ApplyTo(AssetRipper.GUI.Web.GameFileLoader.Settings);
    }
}
