using System.Diagnostics;

namespace ConduentResourceMonitor.Checks;

public class WireGuardCheck : ICheck
{
    private readonly AppSettings _settings;
    private static readonly string WgPath = LocateWg();

    public string Name => "WireGuard";

    public WireGuardCheck(AppSettings settings)
    {
        _settings = settings;
    }

    private static string LocateWg()
    {
        var defaultPath = @"C:\Program Files\WireGuard\wg.exe";
        return File.Exists(defaultPath) ? defaultPath : "wg";
    }

    public async Task<CheckResult> RunAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(WgPath, "show")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start wg.exe");
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return output.Contains($"interface: {_settings.TunnelName}")
                ? new CheckResult(Name, true, "Tunnel active")
                : new CheckResult(Name, false, $"'{_settings.TunnelName}' not active");
        }
        catch (Exception ex)
        {
            return new CheckResult(Name, false, ex.Message);
        }
    }
}
