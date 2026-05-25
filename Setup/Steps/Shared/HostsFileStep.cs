namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class HostsFileStep : ISetupStep
{
    private readonly string _ip;
    private readonly string _hostname;
    private const string HostsFile = @"C:\Windows\System32\drivers\etc\hosts";

    public string Title => "Update Hosts File";
    public string Description => $"Adds the entry\r\n\r\n  {_ip}  {_hostname}\r\n\r\nto {HostsFile}.\r\nRequires administrator access.";
    public bool RequiresElevation => true;
    public bool IsManual => false;
    public bool CanSkip => false;

    public HostsFileStep(string ip, string hostname)
    {
        _ip = ip;
        _hostname = hostname;
    }

    public Task<bool> IsAlreadyCompleteAsync()
    {
        try
        {
            var lines = File.ReadAllLines(HostsFile);
            return Task.FromResult(lines.Any(l =>
                !l.TrimStart().StartsWith('#') &&
                l.Contains(_hostname, StringComparison.OrdinalIgnoreCase) &&
                l.TrimStart().StartsWith(_ip)));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        progress.Report($"Adding '{_ip}  {_hostname}' to hosts file (requires UAC)...");
        var script = $"""
            findstr /C:"{_hostname}" "{HostsFile}" > nul 2>&1
            if errorlevel 1 (
                echo {_ip}  {_hostname} >> "{HostsFile}"
                echo Added: {_ip}  {_hostname}
            ) else (
                echo Entry already exists for {_hostname}
            )
            """;
        await ProcessHelper.RunElevatedBatAsync(script);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "Hosts file updated." : "Could not verify hosts file entry. Check manually.");
    }
}
