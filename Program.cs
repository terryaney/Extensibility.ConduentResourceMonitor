using System.Runtime.InteropServices;
using System.Reflection;
using CommandLine;
using ConduentResourceMonitor;
using ConduentResourceMonitor.Setup;

internal static class Program
{
	private const int AttachParentProcess = -1;
	private const int HelpColumnWidth = 34;

	private static readonly IReadOnlyDictionary<string, OptionHelpMetadata> OptionHelpByProperty =
		typeof( Options )
			.GetProperties( BindingFlags.Public | BindingFlags.Instance )
			.Select( property => new { Property = property, Option = property.GetCustomAttribute<OptionAttribute>() } )
			.Where( x => x.Option is not null )
			.ToDictionary(
				x => x.Property.Name,
				x => BuildOptionMetadata( x.Property, x.Option! ),
				StringComparer.Ordinal );

	private sealed record OptionHelpMetadata( string Flag, string Description, bool Required );

	[DllImport( "kernel32.dll" )]
	private static extern bool AttachConsole( int dwProcessId );

	[STAThread]
	static void Main( string[] args )
	{
		Application.SetHighDpiMode( HighDpiMode.SystemAware );
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault( false );

		if ( IsHelpRequested( args ) )
		{
			ShowHelp( args );
			return;
		}

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

	static bool IsHelpRequested( IEnumerable<string> args ) =>
		args.Any( a => a is "-?" or "/?" or "--help" or "-h" );

	static AppMode? ParseRequestedMode( string[] args )
	{
		var modeIdx = Array.FindIndex( args, a => a.Equals( "--mode", StringComparison.OrdinalIgnoreCase ) );
		if ( modeIdx >= 0 && modeIdx + 1 < args.Length &&
			Enum.TryParse<AppMode>( args[ modeIdx + 1 ], ignoreCase: true, out var mode ) )
			return mode;

		return null;
	}

	static void ShowHelp( string[] args )
	{
		if ( AttachConsole( AttachParentProcess ) )
		{
			using var stdout = new StreamWriter( Console.OpenStandardOutput(), leaveOpen: true ) { AutoFlush = true };
			Console.SetOut( stdout );
			WriteHelp( ParseRequestedMode( args ) );
			return;
		}

		ShowInfo( "Help is only supported in CLI mode. Run this command from a console to view -? output." );
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
			PacPort = options.PacPort ?? settings.PacPort,
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
		var defaults = new AppSettings();
		var defaultCheckUrl = $"Default: {defaults.CheckUrl}";
		var defaultPacDirectory = $"Default: {defaults.PacDirectory}";
		var defaultPacPort = $"Default: {defaults.PacPort}";
		var defaultCheckInterval = $"Default: {defaults.CheckIntervalSeconds}";
		var defaultNotifyTimeout = $"Default: {defaults.NotifyTimeoutMs}";
		var defaultHubTunnelName = $"Default: {defaults.TunnelName}";
		var defaultTunnelName = $"Default: {defaults.TunnelName} (Hub) or Travel-Tunnel (Travel)";
		var defaultConfDirectory = $"Default: {defaults.PacDirectory}";

		static void Opt( string flag, string desc, string? note = null )
		{
			Console.WriteLine( ( "  " + flag ).PadRight( HelpColumnWidth ) + desc );
			if ( note != null )
				Console.WriteLine( "".PadRight( HelpColumnWidth ) + note );
		}

		static void OptFromOption( string propertyName, string? note = null )
		{
			if ( !OptionHelpByProperty.TryGetValue( propertyName, out var option ) )
				throw new InvalidOperationException( $"Missing [Option] metadata for '{propertyName}'." );

			var desc = option.Required ? $"{option.Description} (required)" : option.Description;
			Opt( option.Flag, desc, note );
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
			OptFromOption( nameof( Options.RepairOnStart ), "Use in startup shortcut to handle Windows boot timing." );
			OptFromOption( nameof( Options.CheckUrl ), defaultCheckUrl );
			OptFromOption( nameof( Options.TunnelName ), defaultHubTunnelName );
			OptFromOption( nameof( Options.PacDirectory ), defaultPacDirectory );
			OptFromOption( nameof( Options.PacPort ), defaultPacPort );
			OptFromOption( nameof( Options.CheckIntervalSeconds ), defaultCheckInterval );
			OptFromOption( nameof( Options.NotifyTimeoutMs ), defaultNotifyTimeout );
			OptFromOption( nameof( Options.ShowLog ) );
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
			OptFromOption( nameof( Options.CheckUrl ), defaultCheckUrl );
			OptFromOption( nameof( Options.TunnelName ), "Default: Travel-Tunnel" );
			OptFromOption( nameof( Options.PacDirectory ), defaultPacDirectory );
			OptFromOption( nameof( Options.PacPort ), defaultPacPort );
			OptFromOption( nameof( Options.CheckIntervalSeconds ), defaultCheckInterval );
			OptFromOption( nameof( Options.NotifyTimeoutMs ), defaultNotifyTimeout );
			OptFromOption( nameof( Options.ShowLog ) );
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
			OptFromOption( nameof( Options.Mode ) );
			OptFromOption( nameof( Options.CheckUrl ), defaultCheckUrl );
			OptFromOption( nameof( Options.TunnelName ), defaultTunnelName );
			OptFromOption( nameof( Options.PacDirectory ), defaultPacDirectory );
			OptFromOption( nameof( Options.PacPort ), defaultPacPort );
			OptFromOption( nameof( Options.CheckIntervalSeconds ), defaultCheckInterval );
			OptFromOption( nameof( Options.NotifyTimeoutMs ), defaultNotifyTimeout );
			OptFromOption( nameof( Options.ShowLog ) );
			Console.WriteLine();
			Console.WriteLine( "Hub-Only Monitor Options:" );
			OptFromOption( nameof( Options.RepairOnStart ), "Use in startup shortcut to handle Windows boot timing." );
			Console.WriteLine();
			Console.WriteLine( "Setup Options:" );
			OptFromOption( nameof( Options.Setup ) );
			OptFromOption( nameof( Options.AddTravelConfig ) );
			OptFromOption( nameof( Options.ConfDirectory ), defaultConfDirectory );
			OptFromOption( nameof( Options.ConfFile ) );
			Console.WriteLine();
			Console.WriteLine( "Tip: Run -? --mode Hub or -? --mode Travel for mode-specific help." );
		}

		Console.WriteLine();
	}

	static void ShowError( string message ) => MessageBox.Show( message, "Conduent Resource Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error );

	static void ShowInfo( string message ) => MessageBox.Show( message, "Conduent Resource Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information );

	private static OptionHelpMetadata BuildOptionMetadata( PropertyInfo property, OptionAttribute option )
	{
		var type = Nullable.GetUnderlyingType( property.PropertyType ) ?? property.PropertyType;
		var valueHint = type == typeof( bool ) ? string.Empty : $" <{GetValueHint( type )}>";
		return new OptionHelpMetadata( $"--{option.LongName}{valueHint}", option.HelpText ?? string.Empty, option.Required );
	}

	private static string GetValueHint( Type optionType )
	{
		if ( optionType.IsEnum )
			return string.Join( "|", Enum.GetNames( optionType ) );

		if ( optionType == typeof( int ) ) return "n";
 
		return "value";
	}
}