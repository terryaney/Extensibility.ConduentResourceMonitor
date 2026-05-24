namespace ConduentResourceMonitor.Repairs;

public class ResourcePproxyRepair : IRepair
{
    public string Label => "Resource pproxy";
    public string TargetCheckName => "pproxy";

    public void Execute()
    {
        MessageBox.Show(
            "Remote to the Resource machine and ensure VPN is enabled and that the " +
            "'Conduent-Resource - Resource Provider' terminal profile is running.",
            "Fix: Resource pproxy",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
