using System.Diagnostics;

namespace ConduentResourceMonitor.Repairs;

public class WireGuardRepair( AppSettings settings ) : IRepair
{
	private readonly AppSettings _settings = settings;

	public string Label => "Restart WireGuard";
	public string TargetCheckName => "WireGuard";
	public bool RequiresElevation => true;

	public void Execute()
	{
		var serviceName = $"WireGuardTunnel${_settings.TunnelName}";
		var script = $"sc stop \"{serviceName}\" & timeout /t 3 /nobreak & sc start \"{serviceName}\"";
		Process.Start( new ProcessStartInfo( "cmd.exe", $"/c {script}" )
		{
			Verb = "runas",
			UseShellExecute = true
		} );
	}
}