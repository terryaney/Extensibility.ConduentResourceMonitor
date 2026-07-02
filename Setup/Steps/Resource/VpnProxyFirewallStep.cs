namespace ConduentResourceMonitor.Setup.Steps.Resource;

public class VpnProxyFirewallStep( SetupContext ctx ) : ISetupStep
{
	private const string RuleName = "CRM - VPN Proxy";

	public string Title => "Configure Proxy Firewall Rule";
	public string Description => $"Creates a \"{RuleName}\" Windows Firewall inbound rule allowing TCP connections on port 8888, scoped to the Hub machine only.\r\nRequires administrator access.";
	public bool RequiresElevation => true;
	public bool IsManual => false;
	public IReadOnlyList<SetupInput> Inputs => [ctx.HubStaticIpInput()];

	public async Task<bool> IsAlreadyCompleteAsync()
	{
		var (code, _) = await ProcessHelper.RunAsync( "netsh", $"advfirewall firewall show rule name=\"{RuleName}\"" );
		return code == 0;
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		progress.Report( "Creating scoped firewall rule (requires UAC)..." );

		// Delete-then-add-then-verify (not a bare add): only Hub ever connects to Resource
		// directly — Travel's traffic is forwarded through Hub's WireGuard tunnel — so the rule
		// is scoped to Hub's static LAN IP. Deleting first makes re-running this step
		// self-correcting if Hub's IP ever changes, rather than leaving a stale remoteip behind.
		var commands = new List<ElevatedCommand>
		{
			new()
			{
				FileName = "netsh",
				Arguments = ["advfirewall", "firewall", "delete", "rule", $"name={RuleName}"],
				// netsh exits 1 when no rules match — expected on first-time setup
				SuccessExitCodes = [0, 1],
				Description = "Removing any existing VPN proxy firewall rule"
			},
			new()
			{
				FileName = "netsh",
				Arguments = ["advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=in", "action=allow", "protocol=TCP", "localport=8888", "profile=private", $"remoteip={ctx.HubStaticIp}"],
				Description = "Creating scoped VPN proxy firewall rule"
			},
			new()
			{
				FileName = "netsh",
				Arguments = ["advfirewall", "firewall", "show", "rule", $"name={RuleName}", "verbose"],
				Description = "Verifying VPN proxy firewall rule"
			}
		};

		var (exitCode, output) = await ProcessHelper.RunElevatedCommandsWithOutputAsync( commands, progress.Report );
		var ok = await IsAlreadyCompleteAsync();
		if ( ok )
			return new SetupStepResult( true, "Scoped firewall rule created." );

		if ( exitCode != 0 )
		{
			var message = ProcessHelper.BuildElevatedFailureMessage( "Firewall rule setup", exitCode, output );
			progress.Report( message );
			return new SetupStepResult( false, $"{message}\r\nCheck setup.log." );
		}

		return new SetupStepResult( false, "Could not verify firewall rule. Check setup.log." );
	}
}
