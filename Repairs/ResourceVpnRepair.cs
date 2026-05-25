using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class ResourceVpnRepair( ICheck check ) : IRepair
{
	public string Label => "Check Resource VPN";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => true;

	public void Execute()
	{
		MessageBox.Show(
			"Remote to the Resource machine and ensure VPN is enabled and that the 'Conduent-Resource - Resource Provider' terminal profile is running.",
			"Fix: Check Resource VPN",
			MessageBoxButtons.OK,
			MessageBoxIcon.Information 
		);
	}
}