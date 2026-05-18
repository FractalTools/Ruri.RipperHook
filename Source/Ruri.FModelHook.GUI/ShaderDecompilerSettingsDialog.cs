using System;
using System.Windows;
using System.Windows.Controls;
using AdonisUI.Controls;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.GUI;

// Settings dialog for the ShaderDecompiler module. Inherits AdonisWindow
// so the chrome / colour scheme matches FModel's own dialogs. Click a
// checkbox to toggle — the change is persisted immediately through
// ShaderDecompilerSettingsAccess (which round-trips into the unified
// host config). No Save / Cancel buttons; matches FModel's SettingsView
// idiom of TwoWay-bound IsChecked properties.
internal sealed class ShaderDecompilerSettingsDialog : AdonisWindow
{
    private bool _suppressUpdates;

    public ShaderDecompilerSettingsDialog()
    {
        Title = "Shader Decompiler Settings";
        Width = 520;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        IconVisibility = Visibility.Collapsed;
        EnabledHooksDialog.ApplyAdonisStyle(this);

        ShaderDecompilerSettings current = ShaderDecompilerSettingsAccess.Current;

        StackPanel rows = new() { Margin = new Thickness(12) };

        CheckBox splitVariants = new()
        {
            Content = "Split variants to per-HLSL files (multi-variant stages -> #include distributors)",
            IsChecked = current.SplitVariantsToHlslFiles,
            Margin = new Thickness(0, 8, 0, 4),
        };
        splitVariants.Checked += (_, _) => Apply(splitVariants: true);
        splitVariants.Unchecked += (_, _) => Apply(splitVariants: false);
        TextBlock splitHint = new()
        {
            Text = "When off, every variant body stays inline inside the .shader file under its #if defined(KEYWORD) block. When on, multi-variant stages emit per-variant <stem>/<key>.hlsl files and the .shader uses #include lines. Single-variant stages always inline.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(20, 0, 0, 12),
        };

        CheckBox warnIfNoMappings = new()
        {
            Content = "Warn before exporting shaders without mappings (.usmap)",
            IsChecked = current.WarnIfNoMappings,
            Margin = new Thickness(0, 8, 0, 4),
        };
        warnIfNoMappings.Checked += (_, _) => Apply(warnIfNoMappings: true);
        warnIfNoMappings.Unchecked += (_, _) => Apply(warnIfNoMappings: false);
        TextBlock warnHint = new()
        {
            Text = "Without a type-tree mappings file, every per-material symbol reads as an opaque struct and the resulting .shader files lose all author-facing parameter names.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(20, 0, 0, 12),
        };

        CheckBox tryBaseEngine = new()
        {
            Content = "Fall back to base UE engine metadata when no game-specific folder exists",
            IsChecked = current.TryMatchBaseEngineVersion,
            Margin = new Thickness(0, 8, 0, 4),
        };
        tryBaseEngine.Checked += (_, _) => Apply(tryBaseEngine: true);
        tryBaseEngine.Unchecked += (_, _) => Apply(tryBaseEngine: false);
        TextBlock tryBaseEngineHint = new()
        {
            Text = "When on (default), if no engine-UB metadata folder matches the game's exact EGame name (e.g. GAME_InfinityNikki), the loader scans the base UE folder (e.g. GAME_UE5_4) too. About 99% of games don't customize CB layouts so this is almost always correct and removes the need to hand-seed metadata for every game. Turn off when you suspect a modded engine has drifted layouts and you'd rather see anonymous placeholders than potentially-wrong base-UE names.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(20, 0, 0, 12),
        };

        rows.Children.Add(splitVariants);
        rows.Children.Add(splitHint);
        rows.Children.Add(warnIfNoMappings);
        rows.Children.Add(warnHint);
        rows.Children.Add(tryBaseEngine);
        rows.Children.Add(tryBaseEngineHint);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rows,
        };
    }

    // Each named arg is the new value for that one checkbox; the others
    // are read from the live snapshot so we don't clobber them. Persists
    // through the saver registered by Program.cs.
    private void Apply(bool? splitVariants = null, bool? warnIfNoMappings = null, bool? tryBaseEngine = null)
    {
        if (_suppressUpdates) return;
        ShaderDecompilerSettings current = ShaderDecompilerSettingsAccess.Current;
        ShaderDecompilerSettings updated = new()
        {
            SplitVariantsToHlslFiles = splitVariants ?? current.SplitVariantsToHlslFiles,
            WarnIfNoMappings = warnIfNoMappings ?? current.WarnIfNoMappings,
            TryMatchBaseEngineVersion = tryBaseEngine ?? current.TryMatchBaseEngineVersion,
        };
        ShaderDecompilerSettingsAccess.Replace(updated, persist: true);
    }
}
