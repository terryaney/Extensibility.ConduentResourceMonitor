namespace ConduentResourceMonitor.Setup;

internal class SetupModePicker : Form
{
    private readonly RadioButton _rbHub;
    private readonly RadioButton _rbTravel;
    private readonly RadioButton _rbResource;

    public SetupMode SelectedMode =>
        _rbTravel.Checked ? SetupMode.Travel :
        _rbResource.Checked ? SetupMode.Resource :
        SetupMode.Hub;

    public SetupModePicker()
    {
        Text = "Conduent Resource Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16, 12, 16, 8);

        _rbHub = new RadioButton
        {
            Text = "Hub — Always-on home machine; sets up WireGuard server, firewall, and port forwarding.",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4)
        };
        _rbTravel = new RadioButton
        {
            Text = "Travel — Remote laptop; connects to Hub via WireGuard to reach corporate resources.",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4)
        };
        _rbResource = new RadioButton
        {
            Text = "Resource — Conduent laptop; runs pproxy to share corporate VPN with Hub and Travel.",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4)
        };

        var btnOk = new Button { Text = "Continue →", DialogResult = DialogResult.OK, AutoSize = true };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var btnPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        };
        btnPanel.Controls.AddRange([btnCancel, btnOk]);

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false
        };
        stack.Controls.AddRange([_rbHub, _rbTravel, _rbResource, btnPanel]);

        Controls.Add(stack);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
