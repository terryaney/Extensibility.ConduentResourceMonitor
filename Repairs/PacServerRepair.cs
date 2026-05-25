using ConduentResourceMonitor.Services;

namespace ConduentResourceMonitor.Repairs;

public class PacServerRepair( PacServerService service ) : IRepair
{
	private readonly PacServerService _service = service;

	public string Label => "Restart PAC Server";
	public string TargetCheckName => "PAC";
	public bool RequiresElevation => false;

	public void Execute() => _service.Restart();
}