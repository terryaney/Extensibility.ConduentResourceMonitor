using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduentResourceMonitor;

public class AppSettings
{
	public const string DefaultProxyAddress = "conduent-resource:8888";
	public const string DefaultResourceProxyAddress = "localhost:8888";
	public const string DefaultPacFileName = "conduent-resource.pac";

	public string? Mode { get; set; }  // "Hub", "Travel", or "Resource" — saved so bare exe launch works
	public string CheckUrl { get; set; } = "https://hrspwebtools001.americas.oneacs.com/msl";
	public string ProxyAddress { get; set; } = DefaultProxyAddress;
	public int PacPort { get; set; } = 8080;
	public string PacDirectory { get; set; } = @"C:\BTR\Extensibility\ConduentResource";
	public string TunnelName { get; set; } = "Hub-Tunnel";
	public bool SkipWireGuard { get; set; }  // Hub-only LAN setup: serve PAC/proxy without WireGuard monitoring
	public string ConfFilePath { get; set; } = "";  // Travel: last-used WireGuard .conf path
	public string ResourceStaticIp { get; set; } = "10.0.0.1";  // Travel default; Hub overwrites with actual LAN IP
	public string HubStaticIp { get; set; } = "";  // Resource: set during --setup Resource, pre-fills repeat runs; not used at app runtime
	public int CheckIntervalSeconds { get; set; } = 30;
	public int NotifyTimeoutMs { get; set; } = 5000;
	public string HubSyncPath { get; set; } = "";  // Resource: local folder under the Hub account's OneDrive root
	public string ResourceSyncPath { get; set; } = "";  // Resource: local folder under the Resource account's OneDrive root
	public string SyncIgnorePatterns { get; set; } = "~$*;*.tmp;desktop.ini;Thumbs.db";  // semicolon-delimited file-name patterns
	public bool SyncPaused { get; set; }  // tray-owned; persisted so pause survives restart

	[JsonIgnore]
	public AppMode? AppMode => Enum.TryParse<AppMode>( Mode, ignoreCase: true, out var m ) ? m : null;

	[JsonIgnore]
	public bool SyncConfigured =>
		AppMode == ConduentResourceMonitor.AppMode.Resource &&
		!string.IsNullOrWhiteSpace( HubSyncPath ) &&
		!string.IsNullOrWhiteSpace( ResourceSyncPath );

	[JsonIgnore]
	public string ProxyUrl => BuildProxyUrl( ProxyAddress );

	[JsonIgnore]
	public string PacFileName => DefaultPacFileName;

	[JsonIgnore]
	public string PacUrl => BuildPacUrl( PacPort );

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private static string SettingsPath => Path.Combine( AppContext.BaseDirectory, "ResourceMonitor.settings.json" );

	public static string BuildProxyUrl( string proxyAddress ) => $"http://{proxyAddress}";

	public static string BuildPacUrl( int pacPort ) => $"http://localhost:{pacPort}/{DefaultPacFileName}";

	public static AppSettings Load()
	{
		if ( !File.Exists( SettingsPath ) )
			return new AppSettings();
		try
		{
			return JsonSerializer.Deserialize<AppSettings>( File.ReadAllText( SettingsPath ), JsonOptions )
				?? new AppSettings();
		}
		catch
		{
			return new AppSettings();
		}
	}

	public void Save()
	{
		File.WriteAllText( SettingsPath, JsonSerializer.Serialize( this, JsonOptions ) );
	}

	public void ApplyOverrides( Options options )
	{
		// --mode overrides saved mode; --repair-on-start is CLI-only and never touches settings
		if ( options.Mode.HasValue )
		{
			var newModeStr = options.Mode.Value.ToString();
			// One-time default swap on the transition into Resource — Resource is its own proxy
			// origin, so conduent-resource:8888 (which only resolves via Hub/Travel's hosts file)
			// is meaningless there. Guarded so it never stomps a value the operator later hand-edits.
			if ( newModeStr != Mode && options.Mode.Value == ConduentResourceMonitor.AppMode.Resource && ProxyAddress == DefaultProxyAddress )
				ProxyAddress = DefaultResourceProxyAddress;
			Mode = newModeStr;
		}
		if ( options.CheckUrl != null ) CheckUrl = options.CheckUrl;
		if ( options.TunnelName != null ) TunnelName = options.TunnelName;
		if ( options.PacDirectory != null ) PacDirectory = options.PacDirectory;
		if ( options.PacPort.HasValue ) PacPort = options.PacPort.Value;
		if ( options.CheckIntervalSeconds.HasValue ) CheckIntervalSeconds = options.CheckIntervalSeconds.Value;
		if ( options.NotifyTimeoutMs.HasValue ) NotifyTimeoutMs = options.NotifyTimeoutMs.Value;
	}

	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();

		if ( AppMode == null )
			errors.Add( "Mode is required — select Hub, Travel, or Resource in Settings" );

		if ( string.IsNullOrWhiteSpace( CheckUrl ) )
			errors.Add( "Check URL is required" );
		else if ( !Uri.TryCreate( CheckUrl, UriKind.Absolute, out _ ) )
			errors.Add( $"Check URL is not a valid URL: '{CheckUrl}'" );

		if ( string.IsNullOrWhiteSpace( ProxyAddress ) )
			errors.Add( "Proxy Address is required" );

		// Resource uses neither WireGuard nor PAC serving — these fields are hidden from its
		// Settings UI, so they must not be able to block startup with stale values left over
		// from a prior Hub/Travel configuration on the same shared settings file.
		if ( AppMode != ConduentResourceMonitor.AppMode.Resource )
		{
			if ( string.IsNullOrWhiteSpace( TunnelName ) )
				errors.Add( "Tunnel Name is required" );

			if ( string.IsNullOrWhiteSpace( PacDirectory ) )
				errors.Add( "PAC Directory is required" );
			else if ( !Directory.Exists( PacDirectory ) )
				errors.Add( $"PAC Directory does not exist: '{PacDirectory}'" );

			if ( PacPort is < 1 or > 65535 )
				errors.Add( $"PAC Port must be between 1 and 65535 (got {PacPort})" );
		}

		// Sync folders are Resource-only and optional, but half-configured is always an error —
		// blank both fields to turn the feature off.
		var hubSet = !string.IsNullOrWhiteSpace( HubSyncPath );
		var resSet = !string.IsNullOrWhiteSpace( ResourceSyncPath );
		if ( hubSet != resSet )
			errors.Add( "Hub Sync Path and Resource Sync Path must be set together (blank both to disable folder sync)" );
		else if ( hubSet && resSet )
		{
			if ( !Directory.Exists( HubSyncPath ) )
				errors.Add( $"Hub Sync Path does not exist: '{HubSyncPath}'" );
			if ( !Directory.Exists( ResourceSyncPath ) )
				errors.Add( $"Resource Sync Path does not exist: '{ResourceSyncPath}'" );
			if ( Directory.Exists( HubSyncPath ) && Directory.Exists( ResourceSyncPath ) && ArePathsIdenticalOrNested( HubSyncPath, ResourceSyncPath ) )
				errors.Add( "Hub Sync Path and Resource Sync Path must not be the same folder or nested inside each other" );
		}

		if ( CheckIntervalSeconds < 5 )
			errors.Add( $"Check Interval must be at least 5 seconds (got {CheckIntervalSeconds})" );

		if ( NotifyTimeoutMs < 1000 )
			errors.Add( $"Notify Timeout must be at least 1000ms (got {NotifyTimeoutMs})" );

		return errors;
	}

	private static bool ArePathsIdenticalOrNested( string a, string b )
	{
		var fullA = Path.TrimEndingDirectorySeparator( Path.GetFullPath( a ) );
		var fullB = Path.TrimEndingDirectorySeparator( Path.GetFullPath( b ) );
		return fullA.Equals( fullB, StringComparison.OrdinalIgnoreCase )
			|| fullA.StartsWith( fullB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase )
			|| fullB.StartsWith( fullA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
	}
}