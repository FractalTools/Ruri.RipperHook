using System;
using System.IO;
using System.Windows;
using FModel;
using FModel.Settings;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace Ruri.FModelHook.GUI;

public sealed class ConsoleLogSinkHook : RuriHook
{
    private const string Template = "{Timestamp:HH:mm:ss} [{Level:u3}]: {Message:lj}{NewLine}{Exception}";

    [RetargetMethod(typeof(App), "OnStartup", false, false)]
    public static void OnStartup_After(App self, StartupEventArgs e)
    {
        try
        {
            string logsDir = Path.Combine(UserSettings.Default.OutputDirectory, "Logs");
            string logPath = Path.Combine(logsDir, $"FModel-Log-{DateTime.Now:yyyy-MM-dd}.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console(outputTemplate: Template, theme: AnsiConsoleTheme.Literate)
                .WriteTo.File(outputTemplate: Template, path: logPath)
                .CreateLogger();

            Log.Information("[Ruri.FModelHook.GUI] Console sink attached.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ConsoleLogSinkHook] Failed to attach console sink: {ex.Message}");
        }
    }
}
