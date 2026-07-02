namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class InstallWireGuardStep : ISetupStep
{
	public string Title => "Install WireGuard";
	public string Description => "Installs WireGuard using winget. WireGuard's own installer will prompt for administrator access.";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool RerunWhenComplete => false;

	public async Task<bool> IsAlreadyCompleteAsync()
	{
		var (code, _) = await ProcessHelper.RunAsync( "wg", "--version" );
		return code == 0;
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		progress.Report( "Running: winget install WireGuard.WireGuard ..." );
		var (_, output) = await ProcessHelper.RunAsync( "winget", "install WireGuard.WireGuard --source winget --accept-source-agreements --accept-package-agreements" );
		progress.Report( output );
		
		var ok = await IsAlreadyCompleteAsync();
		return new SetupStepResult( ok, ok ? "WireGuard installed successfully." : "WireGuard install may have failed. Check setup.log." );
	}
}