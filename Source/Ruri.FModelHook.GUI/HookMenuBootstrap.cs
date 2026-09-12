using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdonisUI.Controls;
using FModel;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Config;
using Ruri.ShaderTools;
using AdonisMessageBox = AdonisUI.Controls.MessageBox;
using AdonisMessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using AdonisMessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace Ruri.FModelHook.GUI;

public sealed class HookMenuBootstrap : RuriHook
{
    private static int _runOnceGuard;
    private const string HostConfigFileName = "RuriFModelHook.json";

    [RetargetMethod(typeof(MainWindow), "OnLoaded", true, false)]
    public static void OnLoaded_Before(MainWindow self, object sender, RoutedEventArgs e)
    {
        if (System.Threading.Interlocked.Exchange(ref _runOnceGuard, 1) == 1) return;

        try
        {
            InjectMenu(self);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HookMenu] Failed to inject Hooks menu: {ex.Message}");
        }
    }

    private static void InjectMenu(MainWindow window)
    {
        Menu? menu = FindFirstDescendant<Menu>(window);
        if (menu == null)
        {
            Console.Error.WriteLine("[HookMenu] No top-level Menu found on MainWindow — skipping injection.");
            return;
        }

        MenuItem hooksRoot = new() { Header = "Hooks" };
        hooksRoot.Items.Add(BuildEnabledHooksMenuItem(window));
        hooksRoot.Items.Add(BuildShaderDecompilerSettingsMenuItem(window));
        hooksRoot.Items.Add(new Separator());
        hooksRoot.Items.Add(BuildResetConfigMenuItem(window));
        menu.Items.Add(hooksRoot);
    }

    private static MenuItem BuildEnabledHooksMenuItem(MainWindow window)
    {
        MenuItem item = new() { Header = "Enabled Hooks..." };
        item.Click += (_, _) =>
        {
            string configPath = HostConfigPath();
            HookConfig config = HookConfig.Load(configPath);
            EnabledHooksDialog dialog = new(config, configPath) { Owner = window };
            dialog.ShowDialog();
        };
        return item;
    }

    private static MenuItem BuildShaderDecompilerSettingsMenuItem(MainWindow window)
    {
        MenuItem item = new() { Header = "Shader Decompiler Settings..." };
        item.Click += (_, _) =>
        {
            ShaderDecompilerSettingsDialog dialog = new() { Owner = window };
            dialog.ShowDialog();
        };
        return item;
    }

    private static MenuItem BuildResetConfigMenuItem(MainWindow window)
    {
        MenuItem item = new() { Header = "Reset Config..." };
        item.Click += (_, _) =>
        {
            var confirm = new MessageBoxModel
            {
                Text = "Wipe ALL hook + module settings to defaults? Restart required to take effect.",
                Caption = "Reset Config",
                Icon = AdonisMessageBoxImage.Warning,
                Buttons = MessageBoxButtons.YesNo(),
            };
            AdonisMessageBox.Show(confirm);
            if (confirm.Result != AdonisMessageBoxResult.Yes) return;

            HookConfig.ResetToDefaults(HostConfigPath());
            AdonisMessageBox.Show(new MessageBoxModel
            {
                Text = "Config wiped. Restart FModel for the change to take effect.",
                Caption = "Reset Config",
                Icon = AdonisMessageBoxImage.Information,
                Buttons = new[] { MessageBoxButtons.Ok() },
            });
        };
        return item;
    }

    private static string HostConfigPath()
        => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HostConfigFileName);

    internal static T? FindFirstDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            T? deeper = FindFirstDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
