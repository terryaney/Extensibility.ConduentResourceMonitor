namespace ConduentResourceMonitor.Setup;

public class SetupContext
{
	public const string HubTunnelName = "Hub-Tunnel";

	public string ConfDirectory { get; set; } = @"C:\BTR\Extensibility\ConduentResource";
	public int PacPort { get; set; } = 8080;

	// Hub-specific
	public string ResourceStaticIp { get; set; } = "";
	public string HubPublicIp { get; set; } = "";
	public List<string> TravelMachineNames { get; set; } = [];

	// Resource-specific
	public string HubStaticIp { get; set; } = "";
	public string HubSyncPath { get; set; } = "";
	public string ResourceSyncPath { get; set; } = "";
	public string SyncIgnorePatterns { get; set; } = "";

	// Travel-specific
	public string ConfFilePath { get; set; } = "";
	public string TravelTunnelName => Path.GetFileNameWithoutExtension( ConfFilePath ); // e.g. "Laptop-Tunnel"

	// Input definitions shared by the steps that consume each value. The same input can appear
	// on multiple steps — a value entered early pre-fills later steps that declare it.

	public SetupInput ResourceStaticIpInput() => new()
	{
		Label = "Resource Static IP",
		Placeholder = "e.g. 192.168.158.3",
		Get = () => ResourceStaticIp,
		Set = v => ResourceStaticIp = v,
		Validate = v => SetupInput.RequiredIPv4( v, "Resource Static IP" )
	};

	public SetupInput HubPublicIpInput() => new()
	{
		Label = "Hub Public IP",
		Placeholder = "Fetching...",
		Get = () => HubPublicIp,
		Set = v => HubPublicIp = v,
		Validate = v => SetupInput.Required( v, "Hub Public IP" ),
		AutoFetch = async () =>
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds( 5 ) };
			return ( await client.GetStringAsync( "https://api.ipify.org" ) ).Trim();
		}
	};

	public SetupInput ConfDirectoryInput() => new()
	{
		Label = "Config Directory",
		Kind = SetupInputKind.FolderPath,
		Get = () => ConfDirectory,
		Set = v => ConfDirectory = v,
		Validate = v => SetupInput.Required( v, "Config Directory" )
	};

	public SetupInput TravelMachineNamesInput() => new()
	{
		Label = "Travel Machines",
		Kind = SetupInputKind.MultilineText,
		Placeholder = "One machine name per line (e.g. Laptop)\r\nLeave empty if no Travel machines yet.",
		Get = () => string.Join( Environment.NewLine, TravelMachineNames ),
		Set = v => TravelMachineNames = [.. v.Split( ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )],
		Validate = v =>
		{
			var bad = v.Split( ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
				.FirstOrDefault( n => !ProcessHelper.IsSafeServiceNameToken( n ) );
			return bad == null ? null : $"Invalid machine name '{bad}' — use letters, digits, '.', '_' or '-'.";
		}
	};

	public SetupInput ConfFilePathInput() => new()
	{
		Label = "Config File (.conf)",
		Kind = SetupInputKind.FilePath,
		FileFilter = "WireGuard Config|*.conf",
		Placeholder = @"e.g. C:\BTR\Extensibility\ConduentResource\Laptop-Tunnel.conf",
		Get = () => ConfFilePath,
		Set = v => ConfFilePath = v,
		Validate = v =>
		{
			if ( string.IsNullOrWhiteSpace( v ) ) return "Config file is required.";
			if ( !File.Exists( v ) ) return $"Config file not found: {v}";
			var content = File.ReadAllText( v );
			return !content.Contains( "[Interface]" ) || !content.Contains( "PrivateKey" )
				? "Config file does not appear to be a valid WireGuard configuration."
				: null;
		}
	};

	public SetupInput HubStaticIpInput() => new()
	{
		Label = "Hub Static LAN IP",
		Placeholder = "e.g. 192.168.158.2",
		Get = () => HubStaticIp,
		Set = v => HubStaticIp = v,
		Validate = v => SetupInput.RequiredIPv4( v, "Hub Static LAN IP" )
	};

	public SetupInput HubSyncPathInput() => new()
	{
		Label = "Hub Sync Path",
		Kind = SetupInputKind.FolderPath,
		Placeholder = @"Folder under the Hub account's OneDrive root (blank = sync off)",
		Get = () => HubSyncPath,
		Set = v => HubSyncPath = v,
		Validate = v => string.IsNullOrWhiteSpace( v ) || Directory.Exists( v )
			? null
			: $"Folder not found: {v}"
	};

	public SetupInput ResourceSyncPathInput() => new()
	{
		Label = "Resource Sync Path",
		Kind = SetupInputKind.FolderPath,
		Placeholder = @"Folder under the Resource account's OneDrive root (blank = sync off)",
		Get = () => ResourceSyncPath,
		Set = v => ResourceSyncPath = v,
		Validate = v => string.IsNullOrWhiteSpace( v ) || Directory.Exists( v )
			? null
			: $"Folder not found: {v}"
	};

	public SetupInput SyncIgnorePatternsInput() => new()
	{
		Label = "Sync Ignore Patterns",
		Placeholder = "Semicolon-delimited file-name patterns",
		Get = () => SyncIgnorePatterns,
		Set = v => SyncIgnorePatterns = v
	};

	// Persist wizard-collected inputs so subsequent setup runs pre-populate them.
	public void PersistInputs( SetupMode mode )
	{
		var settings = AppSettings.Load();
		switch ( mode )
		{
			case SetupMode.Hub:
				if ( ResourceStaticIp.Length > 0 ) settings.ResourceStaticIp = ResourceStaticIp;
				if ( ConfDirectory.Length > 0 ) settings.PacDirectory = ConfDirectory;
				break;
			case SetupMode.Travel:
				if ( ConfFilePath.Length > 0 ) settings.ConfFilePath = ConfFilePath;
				break;
			case SetupMode.Resource:
				if ( HubStaticIp.Length > 0 ) settings.HubStaticIp = HubStaticIp;
				// Sync paths persist unconditionally — clearing a path in setup turns the feature off.
				settings.HubSyncPath = HubSyncPath;
				settings.ResourceSyncPath = ResourceSyncPath;
				if ( SyncIgnorePatterns.Length > 0 ) settings.SyncIgnorePatterns = SyncIgnorePatterns;
				break;
		}
		settings.Save();
	}
}
