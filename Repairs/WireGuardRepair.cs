using ConduentResourceMonitor.Checks;
using ConduentResourceMonitor.Setup;

namespace ConduentResourceMonitor.Repairs;

public class WireGuardRepair( AppSettings settings, ICheck check ) : IRepair
{
	private readonly AppSettings _settings = settings;

	public string Label => "Restart WireGuard";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => true;

	public async Task ExecuteAsync( Action<string>? logLine = null )
	{
		if ( !ProcessHelper.IsSafeServiceNameToken( _settings.TunnelName ) )
		{
			logLine?.Invoke( $"WireGuard repair aborted: invalid tunnel name '{_settings.TunnelName}'." );
			return;
		}

		var serviceName = $"WireGuardTunnel${_settings.TunnelName}";
		var commands = new List<ElevatedCommand>
		{
			new()
			{
				FileName = "sc",
				Arguments = ["stop", serviceName],
				SuccessExitCodes = [0, 1062],
				Description = $"Stopping '{serviceName}'"
			},
			new()
			{
				FileName = "powershell.exe",
				Arguments = ["-NoProfile", "-Command", "Start-Sleep -Seconds 3"],
				Description = "Waiting for service shutdown"
			},
			new()
			{
				FileName = "sc",
				Arguments = ["start", serviceName],
				SuccessExitCodes = [0, 1056],
				Description = $"Starting '{serviceName}'"
			}
		};

		logLine?.Invoke( $"Starting elevated WireGuard restart for '{serviceName}'." );
		var (exitCode, output) = await ProcessHelper.RunElevatedCommandsWithOutputAsync( commands, logLine );
		if ( exitCode == 0 )
			logLine?.Invoke( "WireGuard restart completed successfully." );
		else
			logLine?.Invoke( $"WireGuard restart failed with exit code {exitCode}." );

		if ( output.Length == 0 ) logLine?.Invoke( "WireGuard restart returned no console output." );
	}
}