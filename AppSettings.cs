using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConduentResourceMonitor;

public class AppSettings
{
    public string CheckUrl { get; set; } = "https://hrspwebtools001.americas.oneacs.com/msl";
    public string ProxyAddress { get; set; } = "conduent-resource:8888";
    public int PacPort { get; set; } = 8080;
    public string PacDirectory { get; set; } = @"C:\BTR\Extensibility\ConduentResource";
    public string TunnelName { get; set; } = "Hub-Tunnel";
    public int CheckIntervalSeconds { get; set; } = 30;
    public int NotifyTimeoutMs { get; set; } = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string GetSettingsPath(AppMode mode) =>
        Path.Combine(AppContext.BaseDirectory, $"ResourceMonitor.{mode}.settings.json");

    public static AppSettings Load(AppMode mode)
    {
        var path = GetSettingsPath(mode);
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            if (mode == AppMode.Travel)
                defaults.TunnelName = "Travel-Tunnel";
            return defaults;
        }
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppMode mode)
    {
        File.WriteAllText(GetSettingsPath(mode), JsonSerializer.Serialize(this, JsonOptions));
    }

    public void ApplyOverrides(Options options)
    {
        if (options.CheckUrl != null) CheckUrl = options.CheckUrl;
        if (options.TunnelName != null) TunnelName = options.TunnelName;
        if (options.PacDirectory != null) PacDirectory = options.PacDirectory;
        if (options.PacPort.HasValue) PacPort = options.PacPort.Value;
        if (options.CheckIntervalSeconds.HasValue) CheckIntervalSeconds = options.CheckIntervalSeconds.Value;
        if (options.NotifyTimeoutMs.HasValue) NotifyTimeoutMs = options.NotifyTimeoutMs.Value;
    }
}
