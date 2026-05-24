using System.ServiceProcess;

namespace ConduentResourceMonitor.Setup.Steps.Travel;

public class InstallTravelTunnelStep : ISetupStep
{
    private readonly SetupContext _ctx;
    private string TunnelName => _ctx.TravelTunnelName;
    private string ServiceName => $"WireGuardTunnel${TunnelName}";
    private string ConfPath => Path.Combine(_ctx.ConfDirectory, Path.GetFileName(_ctx.ConfFilePath));

    public string Title => "Install Travel WireGuard Service";
    public string Description => $"Installs {TunnelName}.conf as a Windows service.\r\nConf file: {ConfPath}";
    public bool RequiresElevation => true;
    public bool IsManual => false;
    public bool CanSkip => false;

    public InstallTravelTunnelStep(SetupContext ctx) => _ctx = ctx;

    public Task<bool> IsAlreadyCompleteAsync()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        if (!File.Exists(ConfPath))
            return new SetupStepResult(false, $"Config file not found: {ConfPath}");

        progress.Report($"Installing WireGuard service for {TunnelName} (requires UAC)...");
        var script = $"""
            "C:\Program Files\WireGuard\wireguard.exe" /installtunnelservice "{ConfPath}"
            if errorlevel 1 (
                echo ERROR: Failed to install tunnel service.
                pause
            ) else (
                echo Tunnel service installed: {TunnelName}
            )
            """;
        await ProcessHelper.RunElevatedBatAsync(script);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "Travel tunnel service installed." : "Service install may have failed.");
    }
}
