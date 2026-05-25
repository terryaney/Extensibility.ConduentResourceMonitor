namespace ConduentResourceMonitor.Repairs;

public interface IRepair
{
    string Label { get; }
    string TargetCheckName { get; }
    bool RequiresElevation { get; }
    void Execute();
}
