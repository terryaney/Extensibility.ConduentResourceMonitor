namespace ConduentResourceMonitor.Setup.Steps.Resource;

public class InstallPproxyStep : ISetupStep
{
    public string Title => "Install pproxy";
    public string Description => "Installs pproxy via pip. pproxy is the HTTP proxy that exposes the VPN connection on port 8888.";
    public bool RequiresElevation => false;
    public bool IsManual => false;
    public bool CanSkip => false;

    public async Task<bool> IsAlreadyCompleteAsync()
    {
        var (code, _) = await ProcessHelper.RunAsync("pip", "show pproxy");
        return code == 0;
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        progress.Report("Running: pip install pproxy ...");
        var (code, output) = await ProcessHelper.RunAsync("pip", "install pproxy");
        progress.Report(output);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "pproxy installed." : "pip install may have failed. Check output above.");
    }
}
