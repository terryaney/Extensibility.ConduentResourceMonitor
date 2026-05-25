namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class InstallPythonStep : ISetupStep
{
	public string Title => "Install Python";
	public string Description => "Installs Python 3.12 via winget. Required for the PAC file HTTP server.";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool CanSkip => false;

	public async Task<bool> IsAlreadyCompleteAsync()
	{
		var (code, output) = await ProcessHelper.RunAsync( "python", "--version" );
		if ( code != 0 ) return false;
		// Python 3.12.x or higher
		var version = output.Replace( "Python ", "" ).Trim();
		return Version.TryParse( version, out var v ) && v >= new Version( 3, 12 );
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		progress.Report( "Running: winget install Python.Python.3.12 ..." );
		var (_, output) = await ProcessHelper.RunAsync( "winget", "install Python.Python.3.12 --source winget --accept-source-agreements --accept-package-agreements" );
		progress.Report( output );
		progress.Report( "\r\nNote: You may need to restart this application after Python installs to update PATH." );
		
		var ok = await IsAlreadyCompleteAsync();
		return new SetupStepResult( ok, ok ? "Python 3.12+ is installed." : "Python install may have failed or PATH not updated yet. Try restarting the app." );
	}
}