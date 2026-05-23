namespace ConduentResourceMonitor.Repairs;

public interface IRepair
{
    string Label { get; }
    string TargetCheckName { get; }
    void Execute();
}
