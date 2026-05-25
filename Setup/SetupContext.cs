namespace ConduentResourceMonitor.Setup;

public class SetupContext
{
	public string ConfDirectory { get; set; } = @"C:\BTR\Extensibility\ConduentResource";
	public int PacPort { get; set; } = 8080;

	// Hub-specific
	public string ResourceStaticIp { get; set; } = "";
	public string HubPublicIp { get; set; } = "";
	public List<string> TravelMachineNames { get; set; } = [];
	public bool SkipWireGuard { get; set; }

	// Travel-specific
	public string ConfFilePath { get; set; } = "";
	public string TravelTunnelName => Path.GetFileNameWithoutExtension( ConfFilePath ); // e.g. "Laptop-Tunnel"
}