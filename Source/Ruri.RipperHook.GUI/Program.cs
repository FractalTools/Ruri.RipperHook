using System;
using System.IO;
using System.Windows.Forms;
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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        var config = HookConfig.Load(configPath);

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
    }
}
