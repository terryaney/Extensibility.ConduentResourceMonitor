namespace ConduentResourceMonitor.Setup;

public interface ISetupStep
{
    string Title { get; }
    string Description { get; }
    bool RequiresElevation { get; }
    bool IsManual { get; }
    bool CanSkip { get; }
    Task<bool> IsApplicableAsync() => Task.FromResult(true);
    Task<bool> IsAlreadyCompleteAsync();
    Task<SetupStepResult> RunAsync(IProgress<string> progress);
}
