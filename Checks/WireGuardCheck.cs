using System.ServiceProcess;

namespace ConduentResourceMonitor.Checks;

public class WireGuardCheck( AppSettings settings ) : ICheck
{
	private readonly AppSettings _settings = settings;

	public string Name => "WireGuard";

	public Task<CheckResult> RunAsync()
	{
		var serviceName = $"WireGuardTunnel${_settings.TunnelName}";
		try
		{
			using var sc = new ServiceController( serviceName );
			var status = sc.Status;
			return Task.FromResult(
				status == ServiceControllerStatus.Running
					? new CheckResult( Name, true, "Tunnel service running" )
					: new CheckResult( Name, false, $"Tunnel service {status}" )
			);
		}
		catch ( InvalidOperationException )
		{
			return Task.FromResult(
				new CheckResult( Name, false, $"Service '{serviceName}' not found — is tunnel installed?" )
			);
		}
		catch ( Exception ex )
		{
			return Task.FromResult( new CheckResult( Name, false, ex.Message ) );
		}
	}
}