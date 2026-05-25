using System.ServiceProcess;

namespace ConduentResourceMonitor.Setup.Steps.Hub;

public class InstallHubTunnelStep : ISetupStep
{
    private readonly SetupContext _ctx;
    private const string TunnelName = "Hub-Tunnel";
    private string ServiceName => $"WireGuardTunnel${TunnelName}";
    private string ConfPath => Path.Combine(_ctx.ConfDirectory, $"{TunnelName}.conf");

    public string Title => "Install Hub WireGuard Service";
    public string Description => $"Installs Hub-Tunnel.conf as a Windows service so WireGuard starts automatically.\r\nConf file: {ConfPath}";
    public bool RequiresElevation => true;
    public bool IsManual => false;
    public bool CanSkip => false;

    public InstallHubTunnelStep(SetupContext ctx) => _ctx = ctx;

    public Task<bool> IsAlreadyCompleteAsync()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status; // throws if not found
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
            return new SetupStepResult(false, $"Config file not found: {ConfPath}\r\nRun the 'Generate Keys' step first.");

        progress.Report($"Installing WireGuard service for {TunnelName} (requires UAC)...");
        var commands = new List<ElevatedCommand>
        {
            new()
            {
                FileName = ProcessHelper.WireGuardExePath,
                Arguments = ["/installtunnelservice", ConfPath],
                Description = "Installing Hub tunnel service"
            }
        };
        await ProcessHelper.RunElevatedCommandsAsync(commands);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "Hub tunnel service installed." : "Service install may have failed. Check the command window output.");
    }
}
