namespace ConduentResourceMonitor;

public class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly AppMode _mode;
    private readonly Dictionary<string, TextBox> _fields = new();

    public SettingsForm(AppSettings settings, AppMode mode)
    {
        _settings = settings;
        _mode = mode;

        Text = $"Settings ({mode})";
        Size = new Size(580, 340);
        MinimumSize = new Size(580, 340);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
            Padding = new Padding(12, 12, 12, 4),
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddField(layout, "Check URL", nameof(AppSettings.CheckUrl), settings.CheckUrl);
        AddField(layout, "Proxy Address", nameof(AppSettings.ProxyAddress), settings.ProxyAddress);
        AddField(layout, "Tunnel Name", nameof(AppSettings.TunnelName), settings.TunnelName);
        AddField(layout, "PAC Directory", nameof(AppSettings.PacDirectory), settings.PacDirectory);
        AddField(layout, "PAC Port", nameof(AppSettings.PacPort), settings.PacPort.ToString());
        AddField(layout, "Check Interval (s)", nameof(AppSettings.CheckIntervalSeconds), settings.CheckIntervalSeconds.ToString());
        AddField(layout, "Notify Timeout (ms)", nameof(AppSettings.NotifyTimeoutMs), settings.NotifyTimeoutMs.ToString());

        var note = new Label
        {
            Text = "Proxy Address changes take effect on next check. PAC Port/Directory changes require restart.",
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 28
        };
        layout.Controls.Add(note, 0, 7);
        layout.SetColumnSpan(note, 2);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 40,
            Padding = new Padding(8, 4, 8, 4)
        };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        btnOk.Click += (_, _) => SaveSettings();
        btnPanel.Controls.AddRange([btnCancel, btnOk]);

        Controls.Add(layout);
        Controls.Add(btnPanel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void AddField(TableLayoutPanel layout, string label, string key, string value)
    {
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 6, 8, 2) };
        var tb = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 3) };
        _fields[key] = tb;
        layout.Controls.Add(lbl);
        layout.Controls.Add(tb);
    }

    private void SaveSettings()
    {
        _settings.CheckUrl = _fields[nameof(AppSettings.CheckUrl)].Text.Trim();
        _settings.ProxyAddress = _fields[nameof(AppSettings.ProxyAddress)].Text.Trim();
        _settings.TunnelName = _fields[nameof(AppSettings.TunnelName)].Text.Trim();
        _settings.PacDirectory = _fields[nameof(AppSettings.PacDirectory)].Text.Trim();
        if (int.TryParse(_fields[nameof(AppSettings.PacPort)].Text, out var port)) _settings.PacPort = port;
        if (int.TryParse(_fields[nameof(AppSettings.CheckIntervalSeconds)].Text, out var interval)) _settings.CheckIntervalSeconds = interval;
        if (int.TryParse(_fields[nameof(AppSettings.NotifyTimeoutMs)].Text, out var timeout)) _settings.NotifyTimeoutMs = timeout;
        _settings.Save(_mode);
    }
}
