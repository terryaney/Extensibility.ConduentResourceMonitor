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
                l.Contains(_hostname, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        if ( !System.Net.IPAddress.TryParse( _ip, out _ ) )
            return new SetupStepResult( false, $"Invalid IP address: {_ip}" );

        if ( !ProcessHelper.IsSafeHostAliasToken( _hostname ) )
            return new SetupStepResult( false, $"Invalid hostname: {_hostname}" );

        progress.Report($"Writing '{_ip}  {_hostname}' to hosts file (requires UAC)...");
        var commands = new List<ElevatedCommand>
        {
            new()
            {
                FileName = "powershell.exe",
                Arguments =
                [
                    "-NoProfile",
                    "-Command",
                    "$h=$args[0];$ip=$args[1];$name=$args[2];$lines=[IO.File]::ReadAllLines($h);$filtered=@($lines | Where-Object { -not ($_ -notmatch '^\\s*#' -and $_ -imatch [regex]::Escape($name)) });$filtered+=($ip + '  ' + $name);[IO.File]::WriteAllLines($h,$filtered,[System.Text.Encoding]::ASCII)",
                    HostsFile,
                    _ip,
                    _hostname
                ],
                Description = "Updating hosts file entry"
            }
        };
        await ProcessHelper.RunElevatedCommandsAsync(commands);
        var ok = await IsAlreadyCompleteAsync();
        return new SetupStepResult(ok, ok ? "Hosts file updated." : "Could not verify hosts file entry. Check manually.");
    }
}
