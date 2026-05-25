using ConduentResourceMonitor.Services;

namespace ConduentResourceMonitor.Repairs;

public class PacServerRepair : IRepair
{
    private readonly PacServerService _service;

    public string Label => "Restart PAC Server";
    public string TargetCheckName => "PAC";
    public bool RequiresElevation => false;

    public PacServerRepair(PacServerService service)
    {
        _service = service;
    }

    public void Execute() => _service.Restart();
}
