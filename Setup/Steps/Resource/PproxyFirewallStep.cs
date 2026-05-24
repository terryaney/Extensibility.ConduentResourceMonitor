namespace ConduentResourceMonitor.Setup.Steps.Resource;

public class PproxyFirewallStep : ISetupStep
{
    public string Title => "Configure pproxy Firewall Rule";
    public string Description => "Creates a Windows Firewall inbound rule allowing TCP connections on port 8888 (pproxy).\r\nRequires administrator access.";
    public bool RequiresElevation => true;
    public bool IsManual => false;
    public bool CanSkip => false;

    public async Task<bool> IsAlreadyCompleteAsync()
    {
        var (code, _) = await ProcessHelper.RunAsync("netsh", "advfirewall firewall show rule name=\"pproxy\"");
        return code == 0;
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        progress.Report("Creating pproxy firewall rule (requires UAC)...");
        var script = """
            powershell -Command "New-NetFirewallRule -DisplayName 'pproxy' -Direction Inbound -LocalPort 8888 -Protocol TCP -Action Allow"
            echo Firewall rule created.
            """;
        await ProcessHelper.RunElevatedBatAsync(script);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "pproxy firewall rule created." : "Could not verify firewall rule. Check output window.");
    }
}
