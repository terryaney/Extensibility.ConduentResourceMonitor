namespace ConduentResourceMonitor.Setup;

public class SetupChecklistForm : Form
{
    private readonly SetupMode _mode;
    private readonly List<ISetupStep> _steps;
    private readonly List<Label> _iconLabels = [];
    private readonly Label _statusLabel;

    public SetupChecklistForm(SetupMode mode)
    {
        _mode = mode;

        var confDir = @"C:\BTR\Extensibility\ConduentResource";
        var defaultCtx = new SetupContext
        {
            ConfDirectory = confDir,
            ResourceStaticIp = "192.168.1.1",
            HubStaticIp = "192.168.1.2",
            HubPublicIp = "0.0.0.0",
            SkipWireGuard = false,
            TravelMachineNames = [],
            ConfFilePath = mode == SetupMode.Travel
                ? Path.Combine(confDir, "Travel-Tunnel.conf")
                : ""
        };
        _steps = StepFactory.Build(mode, defaultCtx);

        Text = $"Conduent Resource Setup — {mode} Overview";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(640, 520);

        var header = new Label
        {
            Text = $"{mode} Setup — Steps Overview",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0)
        };

        var description = new Label
        {
            Text = "The following steps will be performed during setup.\n" +
                   "Items with checkmarks (✓) are already complete on this machine.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 44,
            Padding = new Padding(12, 4, 12, 8),
            ForeColor = Color.FromArgb(64, 64, 64)
        };

        _statusLabel = new Label
        {
            Text = "Checking current system state...",
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(12, 4, 12, 4),
            ForeColor = Color.FromArgb(96, 96, 96),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic)
        };

        var stepTable = BuildStepTable();
        var stepPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };
        stepPanel.Controls.Add(stepTable);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6)
        };

        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var btnContinue = new Button { Text = "Continue →", AutoSize = true };
        btnContinue.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        btnPanel.Controls.AddRange([btnCancel, btnContinue]);

        Controls.Add(stepPanel);
        Controls.Add(_statusLabel);
        Controls.Add(description);
        Controls.Add(header);
        Controls.Add(btnPanel);

        AcceptButton = btnContinue;
        CancelButton = btnCancel;

        Load += async (_, _) => await CheckInitialStatusesAsync();
    }

    private TableLayoutPanel BuildStepTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = _steps.Count + 1,  // +1 spacer row absorbs extra height
            Padding = new Padding(4),
            AutoSize = false
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < _steps.Count; i++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // spacer absorbs extra height

        foreach (var step in _steps)
        {
            var title = step.CanSkip ? $"(Optional) {step.Title}" : step.Title;

            var iconLbl = new Label
            {
                Text = "○",
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            var titleLbl = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 0, 0)
            };

            _iconLabels.Add(iconLbl);
            table.Controls.Add(iconLbl);
            table.Controls.Add(titleLbl);
        }

        return table;
    }

    private async Task CheckInitialStatusesAsync()
    {
        try
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                var step = _steps[i];
                var idx = i;
                try
                {
                    var complete = await step.IsAlreadyCompleteAsync();
                    if (complete && !IsDisposed)
                    {
                        Invoke(() =>
                        {
                            if (!IsDisposed && idx < _iconLabels.Count)
                            {
                                _iconLabels[idx].Text = "✓";
                                _iconLabels[idx].ForeColor = Color.DarkGreen;
                            }
                        });
                    }
                }
                catch
                {
                    // Leave as ○ if check fails
                }
            }

            if (!IsDisposed)
            {
                Invoke(() =>
                {
                    if (!IsDisposed)
                    {
                        var doneCount = _iconLabels.Count(l => l.Text == "✓");
                        var totalCount = _steps.Count;

                        if (doneCount == 0)
                            _statusLabel.Text = "No steps are currently complete.";
                        else if (doneCount == totalCount)
                        {
                            _statusLabel.Text = "All steps are already complete! You may still run setup to verify.";
                            _statusLabel.ForeColor = Color.DarkGreen;
                        }
                        else
                        {
                            _statusLabel.Text = $"{doneCount} of {totalCount} steps already complete.";
                            _statusLabel.ForeColor = Color.FromArgb(0, 100, 0);
                        }
                    }
                });
            }
        }
        catch
        {
            if (!IsDisposed)
            {
                Invoke(() =>
                {
                    if (!IsDisposed)
                    {
                        _statusLabel.Text = "Unable to check current system state.";
                        _statusLabel.ForeColor = Color.DarkGray;
                    }
                });
            }
        }
    }
}
