using CommandLine;

namespace ConduentResourceMonitor;

public enum AppMode { Hub, Travel }

public enum SetupMode { Hub, Travel, Resource }

public class Options
{
    [Option("mode", Required = false, HelpText = "Hub or Travel. If omitted, inferred from settings file if exactly one exists.")]
    public AppMode? Mode { get; set; }

    // CLI-only — intentionally absent from AppSettings so it is never persisted
    [Option("repair-on-start", Required = false, HelpText = "Hub only: immediately run port proxy repair on launch (use for startup shortcut)")]
    public bool RepairOnStart { get; set; }

    [Option("check-url", Required = false, HelpText = "URL for VPN/pproxy health check")]
    public string? CheckUrl { get; set; }

    [Option("tunnel-name", Required = false, HelpText = "WireGuard tunnel/service name")]
    public string? TunnelName { get; set; }

    [Option("pac-dir", Required = false, HelpText = "Directory containing conduent-resource.pac")]
    public string? PacDirectory { get; set; }

    [Option("pac-port", Required = false, HelpText = "PAC HTTP server port")]
    public int? PacPort { get; set; }

    [Option("check-interval", Required = false, HelpText = "Seconds between checks")]
    public int? CheckIntervalSeconds { get; set; }

    [Option("notify-timeout", Required = false, HelpText = "Notification display time in ms")]
    public int? NotifyTimeoutMs { get; set; }

    [Option("show-log", Required = false, HelpText = "Open log window on startup")]
    public bool ShowLog { get; set; }

    // Setup options
    [Option("setup", Required = false, HelpText = "Run guided setup wizard for Hub, Travel, or Resource")]
    public SetupMode? Setup { get; set; }

    [Option("add-travel-config", Required = false, HelpText = "Add a new Travel machine config to an existing Hub setup")]
    public bool AddTravelConfig { get; set; }

    [Option("conf-dir", Required = false, HelpText = "Directory for WireGuard .conf files (setup mode). Default: C:\\BTR\\Extensibility\\ConduentResource")]
    public string? ConfDirectory { get; set; }

    [Option("conf-file", Required = false, HelpText = "Travel setup: path to the .conf file generated on Hub")]
    public string? ConfFile { get; set; }
}
