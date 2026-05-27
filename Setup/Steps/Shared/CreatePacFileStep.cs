namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class CreatePacFileStep( string confDirectory ) : ISetupStep
{
	private readonly string _confDirectory = confDirectory;
	private string PacPath => Path.Combine( _confDirectory, AppSettings.DefaultPacFileName );

	public string Title => "Create PAC File";
	public string Description => $"Creates the {AppSettings.DefaultPacFileName} proxy auto-config file in:\r\n{_confDirectory}";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool CanSkip => false;

	public Task<bool> IsAlreadyCompleteAsync() => Task.FromResult( File.Exists( PacPath ) );

	public Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		try
		{
			Directory.CreateDirectory( _confDirectory );
			File.WriteAllText( PacPath, BuildPacContent() );
			progress.Report( $"Created: {PacPath}" );
			return Task.FromResult( new SetupStepResult( true, "PAC file created." ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult( new SetupStepResult( false, ex.Message ) );
		}
	}

	private static string BuildPacContent() => string.Join( "\r\n",
	[
		"function FindProxyForURL(url, host) {",
		"    host = host.toLowerCase();",
		"    url = url.toLowerCase();",
		"",
		"    if (host == \"hrsuappba7003\" ||",
		"        shExpMatch(host, \"*.acsgs.com\") ||",
		"        shExpMatch(host, \"*.int.benefitcenter.com\") ||",
		"        shExpMatch(host, \"*.americas.oneacs.com\") ||",
		"        shExpMatch(host, \"*.securep.benefitcenter.com\")) {",
		$"        return \"PROXY {AppSettings.DefaultProxyAddress}\";",
		"    }",
		"",
		"    return \"DIRECT\";",
		"}"
	] );
}