using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class ResourceVpnRepair( ICheck check, bool isLanOnly = false ) : IRepair
{
	public string Label => isLanOnly ? "Check Resource Proxy Connectivity" : "Check Resource VPN";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => true;

	public Task ExecuteAsync( Action<string>? logLine = null )
	{
		logLine?.Invoke( isLanOnly
			? "Resource proxy connectivity repair is manual and requires user action."
			: "Resource VPN repair is manual and requires user action." );
		MessageBox.Show(
			isLanOnly
				? $"Ensure this Hub machine can reach the Resource machine's proxy endpoint on the LAN, verify the Resource-side proxy/VPN path is healthy, and confirm that the 'CRM - VPN Proxy' firewall rule is in place.{Environment.NewLine + Environment.NewLine}netsh advfirewall firewall show rule name=\"CRM - VPN Proxy\" verbose"
				: $"Remote to the Resource machine and ensure that VPN is currently running and enabled and that the 'CRM - VPN Proxy' firewall rule is in place.{Environment.NewLine + Environment.NewLine}netsh advfirewall firewall show rule name=\"CRM - VPN Proxy\" verbose",
			isLanOnly ? "Fix: Check Resource Proxy Connectivity" : "Fix: Check Resource VPN",
			MessageBoxButtons.OK,
			MessageBoxIcon.Information 
		);

		return Task.CompletedTask;
	}
}