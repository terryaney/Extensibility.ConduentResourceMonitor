using ConduentResourceMonitor.Services;
using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class PacServerRepair( PacServerService service, ICheck check ) : IRepair
{
	private readonly PacServerService _service = service;

	public string Label => $"Restart {TargetCheckName}";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => false;

	public void Execute() => _service.Restart();
}