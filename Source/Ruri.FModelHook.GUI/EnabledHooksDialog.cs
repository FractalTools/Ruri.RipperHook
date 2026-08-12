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
        try
        {
            RuriHook.ApplyHooks(_config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HookMenu] ApplyHooks(re-apply) failed: {ex.Message}");
        }
    }

    internal static void ApplyAdonisStyle(AdonisWindow window)
    {
        if (Application.Current?.TryFindResource(typeof(AdonisWindow)) is Style baseStyle)
        {
            window.Style = baseStyle;
        }
    }
}
