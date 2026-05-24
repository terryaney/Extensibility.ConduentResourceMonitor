namespace ConduentResourceMonitor.Setup.Steps.Hub;

public class FirewallRulesStep : ISetupStep
{
    public string Title => "Configure Firewall Rules";
    public string Description => "Adds Windows Firewall inbound rules for ports 8888 (pproxy) and 13389 (RDP to Resource).\r\nRequires administrator access.";
    public bool RequiresElevation => true;
    public bool IsManual => false;
    public bool CanSkip => true;

    public async Task<bool> IsAlreadyCompleteAsync()
    {
        var (code8888, _) = await ProcessHelper.RunAsync("netsh", "advfirewall firewall show rule name=\"WireGuard Port 8888\"");
        var (code13389, _) = await ProcessHelper.RunAsync("netsh", "advfirewall firewall show rule name=\"WireGuard Port 13389\"");
        return code8888 == 0 && code13389 == 0;
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        progress.Report("Adding firewall rules for ports 8888 and 13389 (requires UAC)...");
        var script = """
            netsh advfirewall firewall add rule name="WireGuard Port 8888" protocol=TCP dir=in localport=8888 action=allow
            netsh advfirewall firewall add rule name="WireGuard Port 13389" protocol=TCP dir=in localport=13389 action=allow
            echo Firewall rules added.
            """;
        await ProcessHelper.RunElevatedBatAsync(script);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "Firewall rules configured." : "Could not verify firewall rules. Check output window.");
    }
}
