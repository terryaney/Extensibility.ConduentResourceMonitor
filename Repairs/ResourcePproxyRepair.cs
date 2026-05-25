namespace ConduentResourceMonitor.Repairs;

public class ResourcePproxyRepair( string targetCheckName = "VPN" ) : IRepair
{
	public string Label => "Check Resource VPN";
	public string TargetCheckName => targetCheckName;
	public bool RequiresElevation => true;

	public void Execute()
	{
		MessageBox.Show(
			"Remote to the Resource machine and ensure VPN is enabled and that the " +
			"'Conduent-Resource - Resource Provider' terminal profile is running.",
			"Fix: Check Resource VPN",
			MessageBoxButtons.OK,
			MessageBoxIcon.Information );
	}
}