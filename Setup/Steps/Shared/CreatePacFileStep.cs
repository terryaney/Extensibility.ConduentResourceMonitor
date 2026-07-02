namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class CreatePacFileStep( SetupContext ctx ) : ISetupStep
{
	private string PacPath => Path.Combine( ctx.ConfDirectory, AppSettings.DefaultPacFileName );

	public string Title => "Create PAC File";
	public string Description => $"Creates the {AppSettings.DefaultPacFileName} proxy auto-config file in:\r\n{ctx.ConfDirectory}";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public IReadOnlyList<SetupInput> Inputs => [ctx.ConfDirectoryInput()];

	public Task<bool> IsAlreadyCompleteAsync() => Task.FromResult( File.Exists( PacPath ) );

	public Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		try
		{
			Directory.CreateDirectory( ctx.ConfDirectory );
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
