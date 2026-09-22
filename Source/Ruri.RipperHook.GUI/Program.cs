using System;
using System.IO;
using System.Windows.Forms;
using AssetRipper.Import.Logging;
using Ruri.Hook.Config;
using Ruri.RipperHook;
using Ruri.ShaderTools;
using Ruri.ShaderTools.Unity.ShaderLab;
using Ruri.ShaderTools.Pipeline.Frontend;

namespace Ruri.RipperHook.GUI;

internal static class Program
{
    private const string ConfigFileName = "RuriRipperHook.json";

    [STAThread]
    public static int Main(string[] args)
    {
        Bootstrap.InstallAssemblyResolver();

        Logger.Add(new ConsoleLogger());

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        bool isFirstRun = !File.Exists(configPath);
        var config = HookConfig.Load(configPath);

        if (isFirstRun)
        {
            config.EnabledHooks.Add("SkipStreamingAssetsCopy");
            config.Save(configPath);
        }
        WireModuleSettings(config, configPath);

        Bootstrap.LoadDeclaredModules();
        int enabledHookCountBeforeApply = config.EnabledHooks.Count;
        Bootstrap.ApplyHooks(config);
        if (config.EnabledHooks.Count != enabledHookCountBeforeApply)
        {
            config.Save(configPath);
        }

		Application.Run(new MainForm(config, configPath));
        return 0;
    }

    private static void WireModuleSettings(HookConfig config, string configPath)
    {
        ShaderDecompilerSettings shader = config.GetModuleSettings<ShaderDecompilerSettings>(ShaderDecompilerSettings.ModuleKey) ?? new ShaderDecompilerSettings();
        ShaderDecompilerSettingsAccess.Replace(shader);
        ShaderDecompilerSettingsAccess.RegisterSaver(updated =>
        {
            HookConfig live = HookConfig.Load(configPath);
            live.SetModuleSettings(ShaderDecompilerSettings.ModuleKey, updated);
            live.Save(configPath);
        });

        SettingsDialog.ArSettingsSnapshot? snapshot = config.GetModuleSettings<SettingsDialog.ArSettingsSnapshot>(SettingsDialog.ArSettingsModuleKey);
        snapshot?.ApplyTo(AssetRipper.GUI.Web.GameFileLoader.Settings);
    }
}
