namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class GitProxyStep : ISetupStep
{
	private static readonly string ProxyUrl = AppSettings.BuildProxyUrl( AppSettings.DefaultProxyAddress );
	private const string GitKey = "http.https://tfs.acsgs.com.proxy";

	public string Title => "Configure Git Proxy";
	public string Description => $"Sets the global git proxy for tfs.acsgs.com to route through {AppSettings.DefaultProxyAddress}.";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool CanSkip => false;

	public async Task<bool> IsApplicableAsync()
	{
		var (code, _) = await ProcessHelper.RunAsync( "git", "--version" );
		return code == 0;
	}

	public async Task<bool> IsAlreadyCompleteAsync()
	{
		var (code, output) = await ProcessHelper.RunAsync( "git", $"config --global {GitKey}" );
		return code == 0 && output.Trim().Equals( ProxyUrl, StringComparison.OrdinalIgnoreCase );
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		progress.Report( $"Running: git config --global {GitKey} {ProxyUrl}" );
		var (code, output) = await ProcessHelper.RunAsync( "git", $"config --global {GitKey} {ProxyUrl}" );
		if ( output.Length > 0 ) progress.Report( output );
		var ok = code == 0;
		return new SetupStepResult( ok, ok ? "Git proxy configured." : $"git config failed (exit {code})." );
	}
}