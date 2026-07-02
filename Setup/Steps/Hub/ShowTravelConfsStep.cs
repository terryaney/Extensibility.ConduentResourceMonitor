namespace ConduentResourceMonitor.Setup.Steps.Hub;

public class ShowTravelConfsStep( SetupContext ctx ) : ISetupStep
{
	private readonly SetupContext _ctx = ctx;

	public string Title => "Travel Machine Config Files";
	public bool RequiresElevation => false;
	public bool IsManual => true;

	public string Description
	{
		get
		{
			if ( _ctx.TravelMachineNames.Count == 0 )
				return "No Travel machines were configured. If you add Travel machines later, use --add-travel-config.";

			var lines = new System.Text.StringBuilder();
			lines.AppendLine( "WireGuard config files have been generated for each Travel machine.\r\n" );
			lines.AppendLine( "For each Travel machine, copy its .conf file and run Travel setup:\r\n" );
			foreach ( var name in _ctx.TravelMachineNames )
			{
				var path = Path.Combine( _ctx.ConfDirectory, $"{name}-Tunnel.conf" );
				lines.AppendLine( $"  {name}:" );
				lines.AppendLine( $"    {path}" );
				lines.AppendLine( $"    Then run: ConduentResourceMonitor.exe --setup Travel --conf-file \"{path}\"" );
				lines.AppendLine();
			}
			lines.AppendLine( "When you have noted the above locations, click Next to continue." );
			return lines.ToString();
		}
	}

	public Task<bool> IsAlreadyCompleteAsync()
	{
		// Auto-complete if all conf files exist (user has run this step before)
		if ( _ctx.TravelMachineNames.Count == 0 ) return Task.FromResult( true );
		return Task.FromResult( _ctx.TravelMachineNames.All( n =>
			File.Exists( Path.Combine( _ctx.ConfDirectory, $"{n}-Tunnel.conf" ) ) ) );
	}

	public Task<SetupStepResult> RunAsync( IProgress<string> _ ) =>
		Task.FromResult( new SetupStepResult( true, "Travel config locations noted." ) );
}