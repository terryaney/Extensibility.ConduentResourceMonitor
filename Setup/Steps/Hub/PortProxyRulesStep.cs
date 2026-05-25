namespace ConduentResourceMonitor.Setup.Steps.Hub;

public class PortProxyRulesStep(SetupContext ctx) : ISetupStep
{
    public string Title => "Configure Port Proxy Rules";
    public string Description =>
        $"""
        Creates netsh port proxy rules:
          Hub:8888  → {ConnectHost}:8888   (pproxy / VPN)
          Hub:13389 → {ConnectHost}:3389   (RDP to Resource)

        Requires administrator access.
        """;
    public bool RequiresElevation => true;
    public bool IsManual => false;
    public bool CanSkip => false;

    private string ConnectHost => string.IsNullOrEmpty(ctx.ResourceStaticIp) ? "conduent-resource" : ctx.ResourceStaticIp;

    public async Task<bool> IsAlreadyCompleteAsync()
    {
        var (code, output) = await ProcessHelper.RunAsync("netsh", "interface portproxy show all");
        return code == 0
            && output.Contains("8888")
            && output.Contains("13389")
            && output.Contains(ConnectHost, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        var host = ConnectHost;
        if ( !ProcessHelper.IsSafeV4HostToken( host ) )
            return new SetupStepResult( false, $"Invalid Resource host/IP: {host}" );

        progress.Report($"Setting up port proxy rules to {host} (requires UAC)...");
        var commands = new List<ElevatedCommand>
        {
            new()
            {
                FileName = "netsh",
                Arguments = ["interface", "portproxy", "add", "v4tov4", "listenaddress=0.0.0.0", "listenport=8888", $"connectaddress={host}", "connectport=8888"],
                Description = "Adding port proxy rule for 8888"
            },
            new()
            {
                FileName = "netsh",
                Arguments = ["interface", "portproxy", "add", "v4tov4", "listenaddress=0.0.0.0", "listenport=13389", $"connectaddress={host}", "connectport=3389"],
                Description = "Adding port proxy rule for 13389"
            },
            new()
            {
                FileName = "netsh",
                Arguments = ["interface", "portproxy", "show", "all"],
                Description = "Verifying port proxy rules"
            }
        };
        await ProcessHelper.RunElevatedCommandsAsync(commands);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "Port proxy rules configured." : "Could not verify port proxy rules. Run 'netsh interface portproxy show all' to check.");
    }
}
