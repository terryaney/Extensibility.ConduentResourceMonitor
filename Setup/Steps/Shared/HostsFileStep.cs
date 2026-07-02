namespace ConduentResourceMonitor.Setup.Steps.Shared;

// The IP is a delegate so wizard-collected inputs (Hub passes Resource's static IP input)
// are read at run time, not captured at step-construction time.
public class HostsFileStep( Func<string> ip, string hostname, SetupInput? input = null ) : ISetupStep
{
	private string Ip => ip();
	private readonly string _hostname = hostname;
	private const string HostsFile = @"C:\Windows\System32\drivers\etc\hosts";

	public string Title => "Update Hosts File";
	public string Description => $"Adds the entry\r\n\r\n  {Ip}  {_hostname}\r\n\r\nto {HostsFile}.\r\nRequires administrator access.";
	public bool RequiresElevation => true;
	public bool IsManual => false;
	public IReadOnlyList<SetupInput> Inputs => input is null ? [] : [input];

	public Task<bool> IsAlreadyCompleteAsync()
	{
		try
		{
			var lines = File.ReadAllLines( HostsFile );
			return Task.FromResult(
				lines.Any( l => !l.TrimStart().StartsWith( '#' ) &&
				l.Contains( _hostname, StringComparison.OrdinalIgnoreCase ) )
			);
		}
		catch
		{
			return Task.FromResult( false );
		}
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		var address = Ip;
		if ( !System.Net.IPAddress.TryParse( address, out _ ) )
			return new SetupStepResult( false, $"Invalid IP address: {address}" );

		if ( !ProcessHelper.IsSafeHostAliasToken( _hostname ) )
			return new SetupStepResult( false, $"Invalid hostname: {_hostname}" );

		progress.Report( $"Writing '{address}  {_hostname}' to hosts file (requires UAC)..." );
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
					address,
					_hostname
				],
				Description = "Updating hosts file entry"
			}
		};
		var (exitCode, output) = await ProcessHelper.RunElevatedCommandsWithOutputAsync( commands, progress.Report );
		if ( exitCode != 0 )
		{
			var message = ProcessHelper.BuildElevatedFailureMessage( "Hosts file update", exitCode, output );
			progress.Report( message );
			return new SetupStepResult( false, $"{message}\r\nCheck setup.log." );
		}

		var ok = await IsAlreadyCompleteAsync();
		return new SetupStepResult( ok, ok ? "Hosts file updated." : "Could not verify hosts file entry. Check setup.log." );
	}
}
