using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduentResourceMonitor;

public class AppSettings
{
    public string? Mode { get; set; }  // "Hub" or "Travel" — saved so bare exe launch works
    public string CheckUrl { get; set; } = "https://hrspwebtools001.americas.oneacs.com/msl";
    public string ProxyAddress { get; set; } = "conduent-resource:8888";
    public int PacPort { get; set; } = 8080;
    public string PacDirectory { get; set; } = @"C:\BTR\Extensibility\ConduentResource";
    public string TunnelName { get; set; } = "Hub-Tunnel";
    public string ConfFilePath { get; set; } = "";  // Travel: last-used WireGuard .conf path
    public int CheckIntervalSeconds { get; set; } = 30;
    public int NotifyTimeoutMs { get; set; } = 5000;

    [JsonIgnore]
    public AppMode? AppMode => Enum.TryParse<AppMode>(Mode, ignoreCase: true, out var m) ? m : null;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "ResourceMonitor.settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void ApplyOverrides(Options options)
    {
        // --mode overrides saved mode; --repair-on-start is CLI-only and never touches settings
        if (options.Mode.HasValue) Mode = options.Mode.Value.ToString();
        if (options.CheckUrl != null) CheckUrl = options.CheckUrl;
        if (options.TunnelName != null) TunnelName = options.TunnelName;
        if (options.PacDirectory != null) PacDirectory = options.PacDirectory;
        if (options.PacPort.HasValue) PacPort = options.PacPort.Value;
        if (options.CheckIntervalSeconds.HasValue) CheckIntervalSeconds = options.CheckIntervalSeconds.Value;
        if (options.NotifyTimeoutMs.HasValue) NotifyTimeoutMs = options.NotifyTimeoutMs.Value;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (AppMode == null)
            errors.Add("Mode is required — select Hub or Travel in Settings");

        if (string.IsNullOrWhiteSpace(CheckUrl))
            errors.Add("Check URL is required");
        else if (!Uri.TryCreate(CheckUrl, UriKind.Absolute, out _))
            errors.Add($"Check URL is not a valid URL: '{CheckUrl}'");

        if (string.IsNullOrWhiteSpace(ProxyAddress))
            errors.Add("Proxy Address is required");

        if (string.IsNullOrWhiteSpace(TunnelName))
            errors.Add("Tunnel Name is required");

        if (string.IsNullOrWhiteSpace(PacDirectory))
            errors.Add("PAC Directory is required");
        else if (!Directory.Exists(PacDirectory))
            errors.Add($"PAC Directory does not exist: '{PacDirectory}'");

        if (PacPort is < 1 or > 65535)
            errors.Add($"PAC Port must be between 1 and 65535 (got {PacPort})");

        if (CheckIntervalSeconds < 5)
            errors.Add($"Check Interval must be at least 5 seconds (got {CheckIntervalSeconds})");

        if (NotifyTimeoutMs < 1000)
            errors.Add($"Notify Timeout must be at least 1000ms (got {NotifyTimeoutMs})");

        return errors;
    }
}
