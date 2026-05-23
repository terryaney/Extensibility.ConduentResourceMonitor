namespace ConduentResourceMonitor.Checks;

public interface ICheck
{
    string Name { get; }
    Task<CheckResult> RunAsync();
}
