using CommandLine;

namespace ConduentResourceMonitor;

public enum AppMode { Hub, Travel }

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
}
