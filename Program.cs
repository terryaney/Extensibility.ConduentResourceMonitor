using System.Runtime.InteropServices;
using CommandLine;
using ConduentResourceMonitor;
using ConduentResourceMonitor.Setup;

internal static class Program
{
	[DllImport( "kernel32.dll" )]
	private static extern bool AttachConsole( int dwProcessId );

	[STAThread]
	static void Main( string[] args )
	{
		Application.SetHighDpiMode( HighDpiMode.SystemAware );
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault( false );

		var setupIdx = Array.FindIndex( args, a => a.Equals( "--setup", StringComparison.OrdinalIgnoreCase ) );
		if ( setupIdx >= 0 )
		{
			var hasMode = setupIdx + 1 < args.Length &&
						  Enum.TryParse<SetupMode>( args[ setupIdx + 1 ], ignoreCase: true, out _ );
			if ( !hasMode )
			{
				using var picker = new SetupModePicker();
				if ( picker.ShowDialog() != DialogResult.OK ) return;
				var list = args.ToList();
				list.Insert( setupIdx + 1, picker.SelectedMode.ToString() );
				args = [ .. list ];
			}
		}

		if ( args.Any( a => a is "-?" or "/?" or "--help" or "-h" ) )
		{
			AttachConsole( -1 );
			using var stdout = new StreamWriter( Console.OpenStandardOutput(), leaveOpen: true ) { AutoFlush = true };
			Console.SetOut( stdout );

			AppMode? mode = null;
			var modeIdx = Array.FindIndex( args, a => a.Equals( "--mode", StringComparison.OrdinalIgnoreCase ) );
			if ( modeIdx >= 0 && modeIdx + 1 < args.Length &&
				Enum.TryParse<AppMode>( args[ modeIdx + 1 ], ignoreCase: true, out var m ) )
				mode = m;
			WriteHelp( mode );
			return;
		}

		Parser.Default.ParseArguments<Options>( args )
			.WithParsed( Run )
			.WithNotParsed( errors =>
			{
				var fatal = errors
					.Where( e => e is not HelpRequestedError and not VersionRequestedError )
					.ToList();
				if ( fatal.Count > 0 )
					ShowError( $"Invalid arguments:\n{string.Join( "\n", fatal )}\n\nUsage: ConduentResourceMonitor.exe [--mode Hub|Travel] [options]" );
				Environment.Exit( 1 );
			} );
	}

	static void Run( Options options )
	{
		if ( options.Setup.HasValue )
		{
			RunSetup( options.Setup.Value, options );
			return;
		}

		if ( options.AddTravelConfig )
		{
			Application.Run( new AddTravelConfigForm( options ) );
			return;
		}

		RunMonitor( options );
	}

	static void RunSetup( SetupMode mode, Options options )
	{
		var settings = AppSettings.Load();
		var ctx = new SetupContext
		{
			ConfDirectory = options.ConfDirectory ?? settings.PacDirectory,
			ConfFilePath = options.ConfFile ?? ( mode == SetupMode.Travel ? settings.ConfFilePath : "" )
		};

		// Show checklist overview first (before config) to show what will be done
		using ( var checklist = new SetupChecklistForm( mode ) )
		{
			if ( checklist.ShowDialog() != DialogResult.OK ) return;
		}

		if ( mode == SetupMode.Travel )
		{
			// Auto-detect conf file: scan for installed WireGuardTunnel$* service, derive name from it
			if ( string.IsNullOrEmpty( ctx.ConfFilePath ) && Directory.Exists( ctx.ConfDirectory ) )
			{
				var wgService = System.ServiceProcess.ServiceController.GetServices()
					.FirstOrDefault( s => s.ServiceName.StartsWith( "WireGuardTunnel$", StringComparison.OrdinalIgnoreCase ) );
				if ( wgService != null )
				{
					var tunnelName = wgService.ServiceName[ "WireGuardTunnel$".Length.. ];
					var byService = Path.Combine( ctx.ConfDirectory, tunnelName + ".conf" );
					if ( File.Exists( byService ) ) ctx.ConfFilePath = byService;
				}
				if ( string.IsNullOrEmpty( ctx.ConfFilePath ) )
				{
					var found = Directory.GetFiles( ctx.ConfDirectory, "*.conf" );
					if ( found.Length == 1 ) ctx.ConfFilePath = found[ 0 ];
				}
			}

			// Skip preflight if the steps that need the conf file are already complete
			if ( TravelNeedsPreflight( ctx ) )
			{
				using var preflight = new SetupPreflightForm( mode, ctx );
				if ( preflight.ShowDialog() != DialogResult.OK ) return;
			}
		}
		else if ( mode != SetupMode.Resource )
		{
			using var preflight = new SetupPreflightForm( mode, ctx );
			if ( preflight.ShowDialog() != DialogResult.OK ) return;
		}

		Application.Run( new SetupWizardForm( mode, ctx ) );
	}

	// Returns true if the preflight form needs to be shown to collect the conf file path.
	// False when both conf-dependent steps (VerifyConfFile + InstallTravelTunnel) are already done.
	static bool TravelNeedsPreflight( SetupContext ctx )
	{
		if ( string.IsNullOrEmpty( ctx.ConfFilePath ) ) return true;

		var confInPlace = File.Exists( Path.Combine( ctx.ConfDirectory, Path.GetFileName( ctx.ConfFilePath ) ) );
		if ( !confInPlace ) return true;

		var tunnelName = Path.GetFileNameWithoutExtension( ctx.ConfFilePath );
		try
		{
			using var sc = new System.ServiceProcess.ServiceController( $"WireGuardTunnel${tunnelName}" );
			_ = sc.Status;
		}
		catch
		{
			return true;
		}

		return false;
	}

	static void RunMonitor( Options options )
	{
		var settings = AppSettings.Load();
		settings.ApplyOverrides( options ); // --mode overrides saved mode; --repair-on-start never touches settings

		var errors = settings.Validate();
		if ( errors.Count > 0 )
		{
			// Give the user a chance to fix settings (including picking a mode) before failing
			using var form = new SettingsForm( settings, allowModeChange: true, validationErrors: errors );
			form.ShowDialog();

			errors = settings.Validate();
			if ( errors.Count > 0 )
			{
				ShowError(
					$"Cannot start monitor — configuration problems remain:\n\n" +
					string.Join( "\n", errors.Select( e => $"  • {e}" ) ) );
				Environment.Exit( 1 );
				return;
			}
		}

		Application.Run( new TrayApp( settings, options.ShowLog, options.RepairOnStart ) );
	}

	static void WriteHelp( AppMode? mode )
	{
		const int col = 34; // 2-space indent + 32 chars for flag/padding

		static void Opt( string flag, string desc, string? note = null )
		{
			Console.WriteLine( ( "  " + flag ).PadRight( col ) + desc );
			if ( note != null )
				Console.WriteLine( "".PadRight( col ) + note );
		}

		Console.WriteLine();
		Console.WriteLine( "Conduent Resource Monitor" );
		Console.WriteLine();

		if ( mode == AppMode.Hub )
		{
			Console.WriteLine( "Usage:" );
			Console.WriteLine( "  ConduentResourceMonitor.exe --mode Hub [options]" );
			Console.WriteLine();
			Console.WriteLine( "What Hub Monitors:" );
			Console.WriteLine( "  VPN/pproxy    HTTP to internal Conduent URL via conduent-resource:8888" );
			Console.WriteLine( "  Port Forward  TCP connect to localhost:8888 and localhost:13389" );
			Console.WriteLine( "  WireGuard     Hub-Tunnel service running" );
			Console.WriteLine();
			Console.WriteLine( "Options:" );
			Opt( "--repair-on-start", "Run port proxy repair on launch (60 s delay).",
				"Use in startup shortcut to handle Windows boot timing." );
			Opt( "--check-url <url>", "URL for VPN/pproxy health check.",
				"Default: https://hrspwebtools001.americas.oneacs.com/msl" );
			Opt( "--tunnel-name <name>", "WireGuard tunnel service name. Default: Hub-Tunnel" );
			Opt( "--pac-dir <path>", "Directory containing conduent-resource.pac.",
				@"Default: C:\BTR\Extensibility\ConduentResource" );
			Opt( "--pac-port <n>", "PAC HTTP server port. Default: 8080" );
			Opt( "--check-interval <n>", "Seconds between health checks. Default: 30" );
			Opt( "--notify-timeout <n>", "Notification display time in ms. Default: 5000" );
			Opt( "--show-log", "Open log window on startup." );
			Console.WriteLine();
			Console.WriteLine( "Tip: Run -? without --mode for full help including setup options." );
		}
		else if ( mode == AppMode.Travel )
		{
			Console.WriteLine( "Usage:" );
			Console.WriteLine( "  ConduentResourceMonitor.exe --mode Travel [options]" );
			Console.WriteLine();
			Console.WriteLine( "What Travel Monitors:" );
			Console.WriteLine( "  VPN/pproxy    HTTP to internal Conduent URL via conduent-resource:8888" );
			Console.WriteLine( "  Port Forward  TCP connect to conduent-resource:13389" );
			Console.WriteLine( "  PAC Server    HTTP to localhost:8080/conduent-resource.pac" );
			Console.WriteLine( "  WireGuard     Travel tunnel service running" );
			Console.WriteLine();
			Console.WriteLine( "Options:" );
			Opt( "--check-url <url>", "URL for VPN/pproxy health check.",
				"Default: https://hrspwebtools001.americas.oneacs.com/msl" );
			Opt( "--tunnel-name <name>", "WireGuard tunnel service name. Default: Travel-Tunnel" );
			Opt( "--pac-dir <path>", "Directory containing conduent-resource.pac.",
				@"Default: C:\BTR\Extensibility\ConduentResource" );
			Opt( "--pac-port <n>", "PAC HTTP server port. Default: 8080" );
			Opt( "--check-interval <n>", "Seconds between health checks. Default: 30" );
			Opt( "--notify-timeout <n>", "Notification display time in ms. Default: 5000" );
			Opt( "--show-log", "Open log window on startup." );
			Console.WriteLine();
			Console.WriteLine( "Tip: Run -? without --mode for full help including setup options." );
		}
		else
		{
			Console.WriteLine( "Usage:" );
			Console.WriteLine( "  ConduentResourceMonitor.exe --mode Hub|Travel [options]" );
			Console.WriteLine( "  ConduentResourceMonitor.exe --setup Hub|Travel|Resource [options]" );
			Console.WriteLine( "  ConduentResourceMonitor.exe --add-travel-config" );
			Console.WriteLine( "  ConduentResourceMonitor.exe -? [--mode Hub|Travel]" );
			Console.WriteLine();
			Console.WriteLine( "Monitor Options:" );
			Opt( "--mode <Hub|Travel>", "Select mode. Inferred from settings file if exactly one exists." );
			Opt( "--check-url <url>", "URL for VPN/pproxy health check.",
				"Default: https://hrspwebtools001.americas.oneacs.com/msl" );
			Opt( "--tunnel-name <name>", "WireGuard tunnel/service name.",
				"Default: Hub-Tunnel (Hub) or Travel-Tunnel (Travel)" );
			Opt( "--pac-dir <path>", "Directory containing conduent-resource.pac.",
				@"Default: C:\BTR\Extensibility\ConduentResource" );
			Opt( "--pac-port <n>", "PAC HTTP server port. Default: 8080" );
			Opt( "--check-interval <n>", "Seconds between health checks. Default: 30" );
			Opt( "--notify-timeout <n>", "Notification display time in ms. Default: 5000" );
			Opt( "--show-log", "Open log window on startup." );
			Console.WriteLine();
			Console.WriteLine( "Hub-Only Monitor Options:" );
			Opt( "--repair-on-start", "Run port proxy repair on launch (60 s delay).",
				"Use in startup shortcut to handle Windows boot timing." );
			Console.WriteLine();
			Console.WriteLine( "Setup Options:" );
			Opt( "--setup <Hub|Travel|Resource>", "Run guided setup wizard." );
			Opt( "--add-travel-config", "Add a new Travel machine to an existing Hub config." );
			Opt( "--conf-dir <path>", "Directory for WireGuard .conf files.",
				@"Default: C:\BTR\Extensibility\ConduentResource" );
			Opt( "--conf-file <path>", "Travel setup: path to the .conf file generated on Hub." );
			Console.WriteLine();
			Console.WriteLine( "Tip: Run -? --mode Hub or -? --mode Travel for mode-specific help." );
		}

		Console.WriteLine();
	}

	static void ShowError( string message ) => MessageBox.Show( message, "Conduent Resource Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error );
}