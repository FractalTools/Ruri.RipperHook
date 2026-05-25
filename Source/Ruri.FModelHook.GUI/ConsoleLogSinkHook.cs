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

// FModel.App.OnStartup (App.xaml.cs:114) 用 `#if DEBUG` 决定要不要 WriteTo.Console.
// Release 构建里只写文件, 控制台一片黑 —— 但我们 Ruri.FModelHook.GUI 的 OutputType=Exe,
// 控制台是开的, 用户只能看到 hook 自己的 Console.WriteLine, 看不到 FModel 加载 pak / 解资源的 Log.Information.
//
// postfix 上去 OnStartup, 在 FModel 设完 Log.Logger 之后, 我们再覆写一次, 把 Console sink 加回来.
// 不动 FModel 的 file sink (保留原来的日志文件路径), 只是补一份控制台输出.
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
