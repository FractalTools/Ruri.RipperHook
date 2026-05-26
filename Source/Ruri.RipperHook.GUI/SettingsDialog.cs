using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AssetRipper.Export.Configuration;
using AssetRipper.GUI.Localizations;
using AssetRipper.GUI.Web;
using AssetRipper.GUI.Web.Pages.Settings.DropDown;
using AssetRipper.Import.Configuration;
using AssetRipper.Primitives;
using AssetRipper.Processing.Configuration;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.GUI;

internal sealed class SettingsDialog : Form
{
    private readonly HookConfig _config;
    private readonly string _configPath;
    private readonly HashSet<string> _stagedHooks;
    private readonly List<Action> _applyActions = new();
    private readonly ShaderDecompilerSettings _shaderDraft;

    // AR_* feature hook label + tooltip. hookId 末尾下划线是因为 attr.Version 为空.
    private static readonly (string HookId, string Label, string Hint)[] ArFeatureHooks =
    [
        ("AR_SkipStreamingAssetsCopy_", "Don't copy StreamingAssets into export",
            "Skip the post-export pass that mirrors the original StreamingAssets tree next to your converted output. Independent of \"Skip StreamingAssets at load\" — that one prevents loading them in the first place."),
        ("AR_SkipProcessingAnimation_", "Skip AnimationClip path restoration",
            "Skip AnimationClipConverter.Process. Unity hashes the target-object paths on AnimationClips; brute-forcing them back is expensive. Turn off only when you actually need anim retargeting."),
        ("AR_PrefabOutlining_", "Recreate prefabs (Prefab Outlining)",
            "Deduplicate identical GameObject structures into shared prefabs. Subsumes AR's native EnablePrefabOutlining setting — that bool alone does nothing in current AR; this hook ships the actual processor."),
        ("AR_StaticMeshSeparation_", "Separate static-batched meshes",
            "Reverse Unity's static-batch combine so each instance gets its own mesh. Useful on baked/VRChat scenes. Subsumes AR's native EnableStaticMeshSeparation (same situation — bool only, no processor)."),
    ];

    public SettingsDialog(HookConfig config, string configPath)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _stagedHooks = new HashSet<string>(config.EnabledHooks, StringComparer.OrdinalIgnoreCase);

        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(720, 560);
        Size = new Size(820, 680);
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;

        FullConfiguration cfg = GameFileLoader.Settings;
        _shaderDraft = CloneShaderSettings(ShaderDecompilerSettingsAccess.Current);

        TabControl tabs = new() { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab(cfg));
        tabs.TabPages.Add(BuildExportTab(cfg));

        if (GameFileLoader.IsLoaded)
        {
            Label notice = new()
            {
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = Localization.SettingsCanOnlyBeChangedBeforeLoadingFiles,
                ForeColor = Color.DarkRed,
            };
            Controls.Add(notice);
        }
        Controls.Add(tabs);

        Panel footer = new() { Dock = DockStyle.Bottom, Height = 48 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        Button ok = new() { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        footer.Controls.Add(cancel);
        footer.Controls.Add(ok);
        ok.Click += (_, _) => CommitChanges(cfg);
        Controls.Add(footer);
        AcceptButton = ok;
        CancelButton = cancel;

        Load += (_, _) =>
        {
            cancel.Location = new Point(footer.ClientSize.Width - 200, 12);
            ok.Location = new Point(footer.ClientSize.Width - 100, 12);
        };
        footer.Resize += (_, _) =>
        {
            cancel.Location = new Point(footer.ClientSize.Width - 200, 12);
            ok.Location = new Point(footer.ClientSize.Width - 100, 12);
        };
    }

    private void CommitChanges(FullConfiguration cfg)
    {
        foreach (Action apply in _applyActions) apply();
        _config.EnabledHooks.Clear();
        foreach (string id in _stagedHooks) _config.EnabledHooks.Add(id);
        // Persist AR native settings INTO our unified JSON.
        _config.SetModuleSettings(ArSettingsModuleKey, ArSettingsSnapshot.From(cfg));
        ShaderDecompilerSettingsAccess.Replace(_shaderDraft, persist: true);
        _config.Save(_configPath);
        SerializedSettings.DeleteDefaultPath();
    }

    // ------ General tab ------
    private TabPage BuildGeneralTab(FullConfiguration cfg)
    {
        TabPage page = new("General");
        TableLayoutPanel table = NewTabTable();
        page.Controls.Add(table);

        AddSectionHeader(table, "Streaming assets");
        AddCheckBox(table, Localization.SkipStreamingAssets,
            cfg.ImportSettings.IgnoreStreamingAssets,
            v => cfg.ImportSettings.IgnoreStreamingAssets = v);
        AddArHookCheckbox(table, "AR_SkipStreamingAssetsCopy_");

        AddSectionHeader(table, "Asset processors (hook-driven)");
        AddArHookCheckbox(table, "AR_SkipProcessingAnimation_");
        AddArHookCheckbox(table, "AR_PrefabOutlining_");
        AddArHookCheckbox(table, "AR_StaticMeshSeparation_");

        AddSectionHeader(table, "Assemblies");
        AddCheckBox(table, Localization.RemoveNullableAttributes,
            cfg.ProcessingSettings.RemoveNullableAttributes,
            v => cfg.ProcessingSettings.RemoveNullableAttributes = v);
        AddCheckBox(table, Localization.PublicizeAssemblies,
            cfg.ProcessingSettings.PublicizeAssemblies,
            v => cfg.ProcessingSettings.PublicizeAssemblies = v);
        AddDropDown(table, ScriptContentLevelDropDownSetting.Instance,
            cfg.ImportSettings.ScriptContentLevel,
            v => cfg.ImportSettings.ScriptContentLevel = v);

        AddSectionHeader(table, "Project");
        AddDropDown(table, BundledAssetsExportModeDropDownSetting.Instance,
            cfg.ProcessingSettings.BundledAssetsExportMode,
            v => cfg.ProcessingSettings.BundledAssetsExportMode = v);
        AddTextBox(table, Localization.DefaultVersion,
            cfg.ImportSettings.DefaultVersion.ToString(),
            s => cfg.ImportSettings.DefaultVersion = TryParseUnityVersion(s));
        AddTextBox(table, Localization.TargetVersionForVersionChanging,
            cfg.ImportSettings.TargetVersion.ToString(),
            s => cfg.ImportSettings.TargetVersion = TryParseUnityVersion(s));
        AddCheckBox(table, Localization.EnableAssetDeduplication,
            cfg.ProcessingSettings.EnableAssetDeduplication,
            v => cfg.ProcessingSettings.EnableAssetDeduplication = v);

        return page;
    }

    // ------ Export tab ------
    private TabPage BuildExportTab(FullConfiguration cfg)
    {
        TabPage page = new(Localization.MenuExport);
        TableLayoutPanel table = NewTabTable();
        page.Controls.Add(table);

        AddSectionHeader(table, "Media formats");
        AddDropDown(table, AudioExportFormatDropDownSetting.Instance,
            cfg.ExportSettings.AudioExportFormat, v => cfg.ExportSettings.AudioExportFormat = v);
        AddDropDown(table, ImageExportFormatDropDownSetting.Instance,
            cfg.ExportSettings.ImageExportFormat, v => cfg.ExportSettings.ImageExportFormat = v);
        AddDropDown(table, LightmapTextureExportFormatDropDownSetting.Instance,
            cfg.ExportSettings.LightmapTextureExportFormat, v => cfg.ExportSettings.LightmapTextureExportFormat = v);
        AddDropDown(table, SpriteExportModeDropDownSetting.Instance,
            cfg.ExportSettings.SpriteExportMode, v => cfg.ExportSettings.SpriteExportMode = v);
        AddDropDown(table, TextExportModeDropDownSetting.Instance,
            cfg.ExportSettings.TextExportMode, v => cfg.ExportSettings.TextExportMode = v);

        AddSectionHeader(table, "Shaders");
        AddDropDown(table, ShaderExportModeDropDownSetting.Instance,
            cfg.ExportSettings.ShaderExportMode, v => cfg.ExportSettings.ShaderExportMode = v);

        // Ruri shader hook checkbox directly under the dropdown — its sub-options reveal only while checked.
        CheckBox ruriShaderHook = new()
        {
            Text = "Use RuriShaderDecompiler (overrides AR's Dummy text exporter)",
            AutoSize = true,
            Checked = _stagedHooks.Contains("AR_ShaderDecompiler_"),
            Margin = new Padding(0, 6, 0, 0),
        };
        new ToolTip().SetToolTip(ruriShaderHook,
            "Replaces DummyShaderTextExporter with Ruri.ShaderDecompiler via the AR_ShaderDecompiler hook. Effective when Shader export mode = Dummy. The sub-options below only apply when this is ticked.");

        FlowLayoutPanel subOptions = new()
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(28, 0, 0, 8),
            Visible = ruriShaderHook.Checked,
        };
        CheckBox splitVariants = new()
        {
            Text = "Split variants into per-HLSL files",
            AutoSize = true,
            Checked = _shaderDraft.SplitVariantsToHlslFiles,
            Margin = new Padding(0, 4, 0, 4),
        };
        new ToolTip().SetToolTip(splitVariants,
            "Off (default): every variant body stays inline in one .shader. On: multi-variant stages emit <stem>/<key>.hlsl + #include distributors. Flip on for huge URP/HDRP shaders where a single .shader file is too big for Unity's importer.");
        subOptions.Controls.Add(splitVariants);

        ruriShaderHook.CheckedChanged += (_, _) =>
        {
            subOptions.Visible = ruriShaderHook.Checked;
            if (ruriShaderHook.Checked) _stagedHooks.Add("AR_ShaderDecompiler_");
            else _stagedHooks.Remove("AR_ShaderDecompiler_");
        };
        _applyActions.Add(() => _shaderDraft.SplitVariantsToHlslFiles = splitVariants.Checked);

        AddRawControl(table, ruriShaderHook);
        AddRawControl(table, subOptions);

        AddSectionHeader(table, "Scripts");
        AddDropDown(table, ScriptLanguageVersionDropDownSetting.Instance,
            cfg.ExportSettings.ScriptLanguageVersion, v => cfg.ExportSettings.ScriptLanguageVersion = v);
        AddDropDown(table, ScriptExportModeDropDownSetting.Instance,
            cfg.ExportSettings.ScriptExportMode, v => cfg.ExportSettings.ScriptExportMode = v);
        AddCheckBox(table, Localization.ScriptsUseFullyQualifiedTypeNames,
            cfg.ExportSettings.ScriptTypesFullyQualified, v => cfg.ExportSettings.ScriptTypesFullyQualified = v);

        AddSectionHeader(table, "Other");
        AddCheckBox(table, Localization.ExportUnreadableAssets,
            cfg.ExportSettings.ExportUnreadableAssets, v => cfg.ExportSettings.ExportUnreadableAssets = v);

        return page;
    }

    // ------ Builder helpers ------
    private static TableLayoutPanel NewTabTable()
    {
        TableLayoutPanel table = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddSectionHeader(TableLayoutPanel table, string text)
    {
        Label lab = new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 12, 0, 4),
        };
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(lab, 0, row);
        table.SetColumnSpan(lab, 2);
    }

    private CheckBox AddCheckBox(TableLayoutPanel table, string label, bool initial, Action<bool> onApply)
    {
        CheckBox cb = new()
        {
            Text = label,
            Checked = initial,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 12, 4),
        };
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(cb, 0, row);
        table.SetColumnSpan(cb, 2);
        _applyActions.Add(() => onApply(cb.Checked));
        return cb;
    }

    private void AddArHookCheckbox(TableLayoutPanel table, string hookId)
    {
        HashSet<string> availableIds = new(
            RuriHook.GetAvailableHooks().Select(h => $"{h.Attribute.GameName}_{h.Attribute.Version}"),
            StringComparer.OrdinalIgnoreCase);
        if (!availableIds.Contains(hookId)) return;

        (string id, string label, string hint) = ArFeatureHooks.First(t => t.HookId == hookId);
        CheckBox cb = new()
        {
            Text = label,
            Checked = _stagedHooks.Contains(id),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 12, 4),
        };
        cb.CheckedChanged += (_, _) =>
        {
            if (cb.Checked) _stagedHooks.Add(id);
            else _stagedHooks.Remove(id);
        };
        new ToolTip { AutoPopDelay = 30_000 }.SetToolTip(cb, hint);
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(cb, 0, row);
        table.SetColumnSpan(cb, 2);
    }

    private void AddTextBox(TableLayoutPanel table, string label, string initial, Action<string> onApply)
    {
        Label lab = new() { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 12, 6) };
        TextBox tb = new() { Text = initial, Anchor = AnchorStyles.Left | AnchorStyles.Right, Width = 280, Margin = new Padding(0, 6, 0, 6) };
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(lab, 0, row);
        table.Controls.Add(tb, 1, row);
        _applyActions.Add(() => onApply(tb.Text));
    }

    // Drives label + per-option display from AR's DropDownSetting singletons so the existing
    // localized strings carry over without us maintaining a parallel translation table.
    private void AddDropDown<T>(TableLayoutPanel table, DropDownSetting<T> setting, T initial, Action<T> onApply) where T : struct, Enum
    {
        Label lab = new() { Text = setting.Title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 12, 6) };
        ComboBox cb = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Width = 280,
            Margin = new Padding(0, 6, 0, 6),
            DisplayMember = nameof(DropDownItem<T>.DisplayName),
        };
        DropDownItem<T> selectedItem = default;
        foreach (DropDownItem<T> item in setting.GetValues())
        {
            cb.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(item.Value, initial)) selectedItem = item;
        }
        cb.SelectedItem = selectedItem;
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(lab, 0, row);
        table.Controls.Add(cb, 1, row);
        _applyActions.Add(() =>
        {
            if (cb.SelectedItem is DropDownItem<T> picked) onApply(picked.Value);
        });
    }

    private static void AddRawControl(TableLayoutPanel table, Control control)
    {
        int row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static ShaderDecompilerSettings CloneShaderSettings(ShaderDecompilerSettings src) => new()
    {
        SplitVariantsToHlslFiles = src.SplitVariantsToHlslFiles,
        WarnIfNoMappings = src.WarnIfNoMappings,
        TryMatchBaseEngineVersion = src.TryMatchBaseEngineVersion,
    };

    private static UnityVersion TryParseUnityVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return default;
        try { return UnityVersion.Parse(s); }
        catch { return default; }
    }

    // AR native settings serialised into our unified JSON under this module key.
    // Program.cs loads + applies on startup so this fully replaces AR's own SerializedSettings file.
    internal const string ArSettingsModuleKey = "ArSettings";

    internal sealed class ArSettingsSnapshot
    {
        public ImportSettings? Import { get; set; }
        public ProcessingSettings? Processing { get; set; }
        public ExportSettings? Export { get; set; }

        public static ArSettingsSnapshot From(FullConfiguration cfg) => new()
        {
            Import = cfg.ImportSettings,
            Processing = cfg.ProcessingSettings,
            Export = cfg.ExportSettings,
        };

        public void ApplyTo(FullConfiguration cfg)
        {
            if (Import is not null) cfg.ImportSettings = Import;
            if (Processing is not null) cfg.ProcessingSettings = Processing;
            if (Export is not null) cfg.ExportSettings = Export;
        }
    }
}
