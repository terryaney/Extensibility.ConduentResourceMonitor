namespace ConduentResourceMonitor.Setup;

public interface ISetupStep
{
	string Title { get; }
	string Description { get; }
	bool RequiresElevation { get; }
	bool IsManual { get; }
	// False when repeating a completed step is unsafe or wasteful (key regeneration,
	// duplicate rules, reinstalls) — Next then verifies completion instead of re-running.
	bool RerunWhenComplete => true;
	IReadOnlyList<SetupInput> Inputs => [];
	Task<bool> IsApplicableAsync() => Task.FromResult( true );
	Task<bool> IsAlreadyCompleteAsync();
	Task<SetupStepResult> RunAsync( IProgress<string> progress );
}
