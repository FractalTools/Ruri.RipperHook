using System;
using System.IO;
using System.Windows.Forms;
using Ruri.Hook.Config;
using Ruri.RipperHook;

namespace Ruri.RipperHook.GUI;

internal static class Program
{
    private const string ConfigFileName = "RuriRipperHook.json";

    [STAThread]
    public static int Main(string[] args)
    {
        Bootstrap.InstallAssemblyResolver();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        var config = HookConfig.Load(configPath);
        Bootstrap.ApplyHooks(config);

		Application.Run(new MainForm(config, configPath));
        return 0;
    }
}
