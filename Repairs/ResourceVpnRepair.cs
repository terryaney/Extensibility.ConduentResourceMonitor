using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class ResourceVpnRepair( ICheck check ) : IRepair
{
	public string Label => "Check Resource VPN";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => true;

	public Task ExecuteAsync( Action<string>? logLine = null )
	{
		logLine?.Invoke( "Resource VPN repair is manual and requires user action." );
		MessageBox.Show(
			$"Remote to the Resource machine and ensure VPN is enabled and that the '{AppSettings.ResourceProviderTerminalProfileName}' terminal profile is running.",
			"Fix: Check Resource VPN",
			MessageBoxButtons.OK,
			MessageBoxIcon.Information 
		);

		return Task.CompletedTask;
	}
}