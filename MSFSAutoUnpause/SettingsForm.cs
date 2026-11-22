using System;
using System.Drawing;
using System.Windows.Forms;

namespace MSFSAutoUnpause;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _cfg;

    private NumericUpDown _nudThreshold = null!;
    private CheckBox      _chkNetwork   = null!;
    private CheckBox      _chkStartup   = null!;

    public SettingsForm(AppSettings cfg)
    {
        _cfg = cfg;
        BuildUI();
        LoadValues();
    }

    private void BuildUI()
    {
        Text            = "MSFS Auto Unpause — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(340, 200);
        Font            = new Font("Segoe UI", 9f);

        int y = 18;

        // ── Pause threshold ───────────────────────────────────────────
        AddLabel("Auto-unpause after being paused for:", 16, y);
        y += 24;

        _nudThreshold = new NumericUpDown
        {
            Location = new Point(16, y), Width = 80,
            Minimum = 5, Maximum = 600, Increment = 5, DecimalPlaces = 0
        };
        Controls.Add(_nudThreshold);
        AddLabel("seconds", 104, y + 3);
        y += 36;

        // ── Checkboxes ────────────────────────────────────────────────
        _chkNetwork = new CheckBox
        {
            Text     = "Enable network watchdog (DNS + internet monitoring)",
            Location = new Point(16, y), AutoSize = true
        };
        Controls.Add(_chkNetwork);
        y += 28;

        _chkStartup = new CheckBox
        {
            Text     = "Start MSFS Auto Unpause with Windows",
            Location = new Point(16, y), AutoSize = true
        };
        Controls.Add(_chkStartup);
        y += 36;

        // ── Buttons ───────────────────────────────────────────────────
        var btnSave = new Button
        {
            Text = "Save", Location = new Point(ClientSize.Width - 170, y),
            Width = 75, Height = 28, DialogResult = DialogResult.OK
        };
        var btnCancel = new Button
        {
            Text = "Cancel", Location = new Point(ClientSize.Width - 88, y),
            Width = 75, Height = 28, DialogResult = DialogResult.Cancel
        };
        btnSave.Click += OnSave;
        Controls.Add(btnSave);
        Controls.Add(btnCancel);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label { Text = text, Location = new Point(x, y), AutoSize = true });
    }

    private void LoadValues()
    {
        _nudThreshold.Value = Math.Clamp(_cfg.PauseThresholdSeconds, 5, 600);
        _chkNetwork.Checked = _cfg.NetworkWatchdogEnabled;
        _chkStartup.Checked = AppSettings.GetStartWithWindowsFromRegistry();
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _cfg.PauseThresholdSeconds  = (int)_nudThreshold.Value;
        _cfg.NetworkWatchdogEnabled = _chkNetwork.Checked;
        _cfg.StartWithWindows       = _chkStartup.Checked;
        _cfg.Save();
        Logger.Info($"Settings saved: threshold={_cfg.PauseThresholdSeconds}s " +
                    $"network={_cfg.NetworkWatchdogEnabled} startup={_cfg.StartWithWindows}");
    }
}
