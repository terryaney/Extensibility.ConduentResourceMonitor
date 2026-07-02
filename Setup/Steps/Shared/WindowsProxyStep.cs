using Microsoft.Win32;

namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class WindowsProxyStep( SetupContext ctx ) : ISetupStep
{
	private readonly SetupContext _ctx = ctx;
	private string PacUrl => AppSettings.BuildPacUrl( _ctx.PacPort );

	public string Title => "Configure Windows Proxy Settings";
	public string Description =>
		"Sets Windows to use the PAC file for automatic proxy configuration.\r\n\r\n" +
		$"PAC URL: {PacUrl}\r\n\r\n" +
		$"This routes corporate URLs through {AppSettings.DefaultProxyAddress} while\r\n" +
		"allowing direct connections for everything else.\r\n\r\n" +
		"Note: Chrome may cache proxy settings — visit chrome://net-internals/#proxy to reset if needed.";
	public bool RequiresElevation => false;
	public bool IsManual => false;

	public Task<bool> IsAlreadyCompleteAsync()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey( @"Software\Microsoft\Windows\CurrentVersion\Internet Settings" );

			var url = key?.GetValue( "AutoConfigURL" ) as string;
			return Task.FromResult( url?.Equals( PacUrl, StringComparison.OrdinalIgnoreCase ) == true );
		}
		catch
		{
			return Task.FromResult( false );
		}
	}

	public Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey( @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true )!;
			key.SetValue( "AutoConfigURL", PacUrl );
			key.SetValue( "ProxyEnable", 0, RegistryValueKind.DWord );

			progress.Report( $"Set AutoConfigURL = {PacUrl}" );
			progress.Report( "Note: Settings > Network & internet > Proxy should now show 'Use setup script' enabled." );

			return Task.FromResult( new SetupStepResult( true, "Windows proxy set to use PAC file." ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult( new SetupStepResult( false, ex.Message ) );
		}
	}
}