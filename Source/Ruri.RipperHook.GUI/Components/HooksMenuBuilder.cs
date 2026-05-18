using System;
using System.IO;
using System.Windows.Forms;
using Ruri.Hook.Config;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.GUI.Components;

// Builds and appends a "Hooks" top-level menu to an existing MenuStrip.
// Designed to be called once during MainForm initialisation; each module
// that wants a settings entry-point adds itself here. Adding a new module
// is a single line: drop another `MenuItem` into Build() with a click
// handler that opens its own settings dialog.
//
// Kept generic on purpose so this stays the canonical place for the GUI
// host to surface hook-related configuration — future modules don't need
// to know how the menu strip is wired, just where to add their own item.
internal static class HooksMenuBuilder
{
    public static void Append(MenuStrip menuStrip, IWin32Window owner, string configPath)
    {
        ToolStripMenuItem hooksRoot = new() { Text = "Hooks" };
        hooksRoot.DropDownItems.Add(BuildShaderDecompilerItem(owner));
        hooksRoot.DropDownItems.Add(new ToolStripSeparator());
        hooksRoot.DropDownItems.Add(BuildResetConfigItem(owner, configPath));
        // Future modules slot in ABOVE the separator; one line per submenu.
        // The owner is forwarded so dialogs centre over MainForm.
        menuStrip.Items.Add(hooksRoot);
    }

    private static ToolStripMenuItem BuildResetConfigItem(IWin32Window owner, string configPath)
    {
        ToolStripMenuItem item = new() { Text = "Reset Config..." };
        item.Click += (_, _) =>
        {
            DialogResult r = MessageBox.Show(
                owner,
                "Wipe ALL hook + module settings to defaults? Restart required to take effect.",
                "Reset Config",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                HookConfig.ResetToDefaults(configPath);
                MessageBox.Show(owner, "Config wiped. Restart for the change to take effect.", "Reset Config", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        return item;
    }

    private static ToolStripMenuItem BuildShaderDecompilerItem(IWin32Window owner)
    {
        ToolStripMenuItem item = new() { Text = "Shader Decompiler Settings..." };
        item.Click += (_, _) =>
        {
            // Click-to-toggle dialog persists every flip on the spot —
            // no working copy, no commit step.
            using ShaderDecompilerSettingsForm dialog = new();
            dialog.ShowDialog(owner);
        };
        return item;
    }
}

// WinForms settings dialog. Click-to-toggle saves immediately through
// ShaderDecompilerSettingsAccess — no Save/Cancel buttons, matching the
// FModel-side dialog.
//
// Unity-only surface: WarnIfNoMappings is intentionally NOT exposed here
// because `.usmap` is an Unreal type-tree mappings concept. AssetRipper /
// Unity decompilation has no analogue, so the option would be a confusing
// no-op in this host. The field still exists on ShaderDecompilerSettings
// (the POCO is shared) — the FModel-side dialog drives it; the value
// loaded here is just preserved on round-trip.
internal sealed class ShaderDecompilerSettingsForm : Form
{
    public ShaderDecompilerSettingsForm()
    {
        Text = "Shader Decompiler Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new System.Drawing.Size(560, 180);

        ShaderDecompilerSettings current = ShaderDecompilerSettingsAccess.Current;

        CheckBox splitVariants = new()
        {
            Text = "Split variants to per-HLSL files (multi-variant stages -> #include distributors)",
            Checked = current.SplitVariantsToHlslFiles,
            AutoSize = true,
            Location = new System.Drawing.Point(16, 16),
        };
        splitVariants.CheckedChanged += (_, _) => Apply(splitVariants: splitVariants.Checked);
        Label splitHint = new()
        {
            Text = "When off, every variant body stays inline inside the .shader file under its #if defined(KEYWORD) block. When on, multi-variant stages emit per-variant <stem>/<key>.hlsl files and the .shader uses #include lines. Single-variant stages always inline.",
            Location = new System.Drawing.Point(36, 42),
            AutoSize = false,
            Width = 510,
            Height = 80,
            ForeColor = System.Drawing.Color.Gray,
        };

        Controls.Add(splitVariants);
        Controls.Add(splitHint);
    }

    // Each named arg is the new value for that one checkbox; the others
    // are read from the live snapshot so we don't clobber them. Persists
    // through the saver registered by Program.cs (re-reads + re-writes
    // the unified config so module-foreign keys stay intact).
    private static void Apply(bool? splitVariants = null)
    {
        ShaderDecompilerSettings current = ShaderDecompilerSettingsAccess.Current;
        ShaderDecompilerSettings updated = new()
        {
            SplitVariantsToHlslFiles = splitVariants ?? current.SplitVariantsToHlslFiles,
            WarnIfNoMappings = current.WarnIfNoMappings,
            TryMatchBaseEngineVersion = current.TryMatchBaseEngineVersion,
        };
        ShaderDecompilerSettingsAccess.Replace(updated, persist: true);
    }
}
