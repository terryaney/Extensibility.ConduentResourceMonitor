using System.ServiceProcess;

namespace ConduentResourceMonitor.Setup.Steps.Travel;

public class InstallTravelTunnelStep( SetupContext ctx ) : ISetupStep
{
	private readonly SetupContext _ctx = ctx;
	private string TunnelName => _ctx.TravelTunnelName;
	private string ServiceName => $"WireGuardTunnel${TunnelName}";
	private string ConfPath => Path.Combine( _ctx.ConfDirectory, Path.GetFileName( _ctx.ConfFilePath ) );

	public string Title => "Install Travel WireGuard Service";
	public string Description => $"Installs {TunnelName}.conf as a Windows service.\r\nConf file: {ConfPath}";
	public bool RequiresElevation => true;
	public bool IsManual => false;
	public bool CanSkip => false;

	public Task<bool> IsAlreadyCompleteAsync()
	{
		try
		{
			using var sc = new ServiceController( ServiceName );
			_ = sc.Status;
			return Task.FromResult( true );
		}
		catch
		{
			return Task.FromResult( false );
		}
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		if ( !File.Exists( ConfPath ) )
			return new SetupStepResult( false, $"Config file not found: {ConfPath}" );

		progress.Report( $"Installing WireGuard service for {TunnelName} (requires UAC)..." );
		var commands = new List<ElevatedCommand>
		{
			new()
			{
				FileName = ProcessHelper.WireGuardExePath,
				Arguments = ["/installtunnelservice", ConfPath],
				Description = $"Installing Travel tunnel service '{TunnelName}'"
			}
		};
		await ProcessHelper.RunElevatedCommandsAsync( commands );

		var ok = await IsAlreadyCompleteAsync();
		return new SetupStepResult( ok, ok ? "Travel tunnel service installed." : "Service install may have failed." );
	}
}