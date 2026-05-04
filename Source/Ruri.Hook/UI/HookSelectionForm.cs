using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Ruri.Hook.Config;
using Ruri.Hook;

namespace Ruri.Hook.UI;

public class HookSelectionForm : Form
{
    private FlowLayoutPanel _panelHooks;
    private Button _btnSaveAndLaunch;
    private Button _btnCancel;
    private readonly HookConfig _config;
    private readonly string _configPath;

    public HookSelectionForm(HookConfig config, string configPath)
    {
        _config = config;
        _configPath = configPath;

        Text = "Ruri Hook Configuration";
        Size = new Size(600, 500);
        StartPosition = FormStartPosition.CenterScreen;

        InitializeComponent();
        LoadHooks();
    }

    private void InitializeComponent()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(10)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 10));

        _panelHooks = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };

        _btnSaveAndLaunch = new Button
        {
            Text = "Save & Launch",
            AutoSize = true,
        };
        _btnSaveAndLaunch.Click += (s, e) => SaveAndClose();

        _btnCancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
        };
        _btnCancel.Click += (s, e) => Application.Exit();

        buttonPanel.Controls.Add(_btnSaveAndLaunch);
        buttonPanel.Controls.Add(_btnCancel);

        layout.Controls.Add(_panelHooks, 0, 0);
        layout.Controls.Add(buttonPanel, 0, 1);

        Controls.Add(layout);
    }

    private void LoadHooks()
    {
        var hooks = RuriHook.GetAvailableHooks();
        var enabledHooks = _config.EnabledHooks;

        foreach (var (type, attr) in hooks)
        {
            var displayName = attr.GameName;
            if (!string.IsNullOrEmpty(attr.Version))
            {
                displayName += $" {attr.Version}";
            }
            if (!string.IsNullOrEmpty(attr.BaseEngineVersion)) 
            {
               displayName += $" [{attr.BaseEngineVersion}]"; 
            }
            
            // Use GameName_Version as ID
            var id = $"{attr.GameName}_{attr.Version}";
            
            var checkBox = new CheckBox
            {
                Text = displayName,
                Tag = id,
                Checked = enabledHooks.Contains(id),
                AutoSize = true,
                Font = new Font("Consolas", 10)
            };
            
            _panelHooks.Controls.Add(checkBox);
        }
    }

    private void SaveAndClose()
    {
        _config.EnabledHooks.Clear();
        
        foreach (CheckBox item in _panelHooks.Controls.OfType<CheckBox>())
        {
            if (item.Checked)
            {
                _config.EnabledHooks.Add(item.Tag.ToString());
            }
        }

        _config.Save(_configPath);
        Close();
    }
}
