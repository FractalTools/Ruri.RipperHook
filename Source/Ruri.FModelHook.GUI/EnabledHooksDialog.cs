using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Config;

namespace Ruri.FModelHook.GUI;

// Programmatic AdonisWindow dialog: a checkbox per discovered hook.
// Click-to-toggle persists immediately to the unified HookConfig file —
// matches FModel's own SettingsView pattern (every IsChecked is
// `Mode=TwoWay UpdateSourceTrigger=PropertyChanged`, so flipping the
// box writes to UserSettings the same instant). No Save/Cancel buttons.
//
// MonoMod hooks are installed at startup and can't be unhooked safely
// mid-session, so a banner at the top reminds the user that toggling a
// hook only takes effect on the next launch.
internal sealed class EnabledHooksDialog : AdonisWindow
{
    private readonly HookConfig _config;
    private readonly string _configPath;
    private readonly StackPanel _list;

    public EnabledHooksDialog(HookConfig config, string configPath)
    {
        _config = config;
        _configPath = configPath;
        Title = "Enabled Hooks";
        Width = 480;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        IconVisibility = Visibility.Collapsed;
        ApplyAdonisStyle(this);

        Grid grid = new();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        TextBlock note = new()
        {
            Margin = new Thickness(12, 12, 12, 8),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Text = "Click a hook to toggle. Changes save and apply immediately.",
        };
        Grid.SetRow(note, 0);
        grid.Children.Add(note);

        _list = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        ScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        Content = grid;
        PopulateList();
    }

    private void PopulateList()
    {
        // Group by GameName like the RipperHook + FModel SettingsView style;
        // versions of the same hook live next to each other under one header.
        var grouped = RuriHook.GetAvailableHooks()
            .GroupBy(h => h.Attribute.GameName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        bool first = true;
        foreach (var group in grouped)
        {
            if (!first)
            {
                _list.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
            }
            first = false;

            var versions = group.OrderBy(h => h.Attribute.Version, StringComparer.OrdinalIgnoreCase).ToList();
            // Single-version hooks: emit one row labelled by GameName so
            // the user doesn't see "Default" wedged under a redundant header.
            // Multi-version: use a header row + indented per-version rows.
            if (versions.Count == 1)
            {
                _list.Children.Add(BuildRow(versions[0].Attribute, contentOverride: group.Key, indent: 0));
            }
            else
            {
                TextBlock header = new()
                {
                    Text = group.Key,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 4, 0, 2),
                };
                _list.Children.Add(header);
                foreach (var (_, attr) in versions)
                {
                    _list.Children.Add(BuildRow(attr, contentOverride: null, indent: 16));
                }
            }
        }
    }

    private CheckBox BuildRow(GameHookAttribute attr, string? contentOverride, double indent)
    {
        string id = $"{attr.GameName}_{attr.Version}";
        string label = contentOverride ?? (string.IsNullOrEmpty(attr.Version) ? "Default" : attr.Version);
        if (!string.IsNullOrEmpty(attr.BaseEngineVersion))
        {
            label += $"  [{attr.BaseEngineVersion}]";
        }

        CheckBox cb = new()
        {
            Content = label,
            IsChecked = _config.EnabledHooks.Contains(id),
            Tag = id,
            Margin = new Thickness(indent, 4, 0, 4),
        };
        // Auto-apply on toggle: mirrors FModel SettingsView's TwoWay
        // bindings — flip the box, the change lives in the file before
        // the user even moves the cursor.
        cb.Checked += (_, _) => Toggle(id, true);
        cb.Unchecked += (_, _) => Toggle(id, false);
        return cb;
    }

    private void Toggle(string id, bool enabled)
    {
        bool changed;
        if (enabled) changed = _config.EnabledHooks.Add(id);
        else changed = _config.EnabledHooks.Remove(id);
        if (!changed) return;

        _config.Save(_configPath);
        // Re-apply hooks against the new EnabledHooks set, mirroring
        // Ruri.RipperHook.GUI.MainForm.ApplyHookConfigurationAsync. This
        // INSTALLs newly-enabled hooks at runtime; previously-installed
        // detours stay live (MonoMod doesn't unhook safely mid-session)
        // but the user's expectation is "change takes effect now",
        // matching RipperHook's behaviour.
        try
        {
            RuriHook.ApplyHooks(_config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HookMenu] ApplyHooks(re-apply) failed: {ex.Message}");
        }
    }

    // Pulls AdonisWindow's implicit style out of Application.Resources
    // (loaded by FModel's App.xaml from AdonisUI.ClassicTheme) and binds
    // it onto our window. Mirrors the `Style BasedOn={x:Type AdonisWindow}`
    // pattern FModel's XAML windows use.
    internal static void ApplyAdonisStyle(AdonisWindow window)
    {
        if (Application.Current?.TryFindResource(typeof(AdonisWindow)) is Style baseStyle)
        {
            window.Style = baseStyle;
        }
    }
}
