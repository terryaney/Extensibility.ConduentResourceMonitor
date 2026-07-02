using ConduentResourceMonitor.Checks;
using ConduentResourceMonitor.Services;

namespace ConduentResourceMonitor.Repairs;

public class ProxyServerRepair( ProxyServerService service, ICheck check ) : IRepair
{
	private readonly ProxyServerService _service = service;

	public string Label => $"Restart {TargetCheckName}";
	public string TargetCheckName => check.Name; // "VPN Proxy"
	public bool RequiresElevation => false;        // eligible for auto-repair

	public Task ExecuteAsync( Action<string>? logLine = null )
	{
		logLine?.Invoke( "Restarting native proxy listener." );
		_service.Restart();
		return Task.CompletedTask;
	}
}
