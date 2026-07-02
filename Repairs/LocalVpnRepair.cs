using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class LocalVpnRepair( ICheck check ) : IRepair
{
	public string Label => "Enable VPN";
	public string TargetCheckName => check.Name; // "VPN Enabled"
	public bool RequiresElevation => true;         // manual only — never auto-popup

	public Task ExecuteAsync( Action<string>? logLine = null )
	{
		logLine?.Invoke( "VPN repair is manual and requires user action." );
		MessageBox.Show(
			"Log into VPN on this machine.",
			"Fix: Enable VPN",
			MessageBoxButtons.OK,
			MessageBoxIcon.Information
		);

		return Task.CompletedTask;
	}
}
