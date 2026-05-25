using ConduentResourceMonitor.Checks;
using ConduentResourceMonitor.Setup;

namespace ConduentResourceMonitor.Repairs;

public class PortProxyRepair( AppSettings settings, ICheck check ) : IRepair
{
	private readonly AppSettings _settings = settings;

	public string Label => "Repair Port Proxy Rules";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => true;

	public Task ExecuteAsync( Action<string>? logLine = null ) => ExecuteAsync( startupDelay: false, logLine );

	public async Task ExecuteAsync( bool startupDelay, Action<string>? logLine = null )
	{
		if ( !ProcessHelper.TryExtractHostFromProxyAddress( _settings.ProxyAddress, out var connectHost, out var error ) )
		{
			logLine?.Invoke( $"Port proxy repair aborted: {error}" );
			return;
		}
		if ( !ProcessHelper.IsSafeV4HostToken( connectHost ) )
		{
			logLine?.Invoke( $"Port proxy repair aborted: invalid host '{connectHost}'." );
			return;
		}

		logLine?.Invoke( $"Starting elevated port proxy repair for host '{connectHost}'." );
		var (exitCode, output) = await ProcessHelper.RunElevatedCommandsWithOutputAsync( BuildCommands( connectHost, startupDelay ), logLine );
		if ( exitCode == 0 )
			logLine?.Invoke( "Port proxy repair completed successfully." );
		else
			logLine?.Invoke( $"Port proxy repair failed with exit code {exitCode}." );

		if ( output.Length == 0 ) logLine?.Invoke( "Port proxy repair returned no console output." );
	}

	private static List<ElevatedCommand> BuildCommands( string connectHost, bool startupDelay )
	{
		var commands = new List<ElevatedCommand>();

		if ( startupDelay )
			commands.Add( SleepCommand( 60, "Waiting for network stack to settle" ) );

		commands.Add(
			new()
			{
				FileName = "sc",
				Arguments = ["stop", "iphlpsvc"],
				SuccessExitCodes = [0, 1062],
				Description = "Stopping IP Helper service"
			} );

		commands.Add( SleepCommand( 10, "Allowing service stop to settle" ) );
		commands.AddRange(
		[
			new()
			{
				FileName = "sc",
				Arguments = ["start", "iphlpsvc"],
				SuccessExitCodes = [0, 1056],
				Description = "Starting IP Helper service"
			},
			SleepCommand( 15, "Allowing service start to settle" ),
			new()
			{
				FileName = "netsh",
				Arguments = ["interface", "portproxy", "reset"],
				Description = "Resetting existing port proxy rules"
			},
			new()
			{
				FileName = "netsh",
				Arguments = ["interface", "portproxy", "add", "v4tov4", "listenaddress=0.0.0.0", "listenport=8888", $"connectaddress={connectHost}", "connectport=8888"],
				Description = "Adding 8888 proxy rule"
			},
			new()
			{
				FileName = "netsh",
				Arguments = ["interface", "portproxy", "add", "v4tov4", "listenaddress=0.0.0.0", "listenport=13389", $"connectaddress={connectHost}", "connectport=3389"],
				Description = "Adding 13389 proxy rule"
			},
			new()
			{
				FileName = "netsh",
				Arguments = ["interface", "portproxy", "show", "all"],
				Description = "Verifying configured port proxy rules"
			},
			new()
			{
				FileName = "powershell.exe",
				Arguments = ["-NoProfile", "-Command", "$o=netstat -an; if(($o -match ':8888') -and ($o -match ':13389')) { exit 0 } else { exit 1 }"],
				Description = "Verifying 8888 and 13389 listeners are active"
			}
		]);

		return commands;
	}

	private static ElevatedCommand SleepCommand( int seconds, string description ) => new()
	{
		FileName = "powershell.exe",
		Arguments = ["-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}"],
		Description = description
	};
}