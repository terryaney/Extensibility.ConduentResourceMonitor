using ConduentResourceMonitor.Setup.Steps.Hub;
using ConduentResourceMonitor.Setup.Steps.Resource;
using ConduentResourceMonitor.Setup.Steps.Shared;
using ConduentResourceMonitor.Setup.Steps.Travel;

namespace ConduentResourceMonitor.Setup;

internal static class StepFactory
{
	public static List<ISetupStep> Build( SetupMode mode, SetupContext ctx ) => mode switch
	{
		SetupMode.Hub => BuildHub( ctx ),
		SetupMode.Travel => BuildTravel( ctx ),
		SetupMode.Resource => BuildResource( ctx ),
		_ => throw new ArgumentOutOfRangeException( nameof( mode ) )
	};

	// The WireGuard steps are always present — a LAN-only Hub just skips them; Program.cs
	// derives the SkipWireGuard runtime setting from what was skipped in the wizard.
	private static List<ISetupStep> BuildHub( SetupContext ctx ) =>
	[
		new RouterConfirmStep(ctx),
		new InstallWireGuardStep(),
		new GenerateAllKeysStep(ctx),
		new InstallHubTunnelStep(ctx),
		new FirewallRulesStep(),
		new PortProxyRulesStep(ctx),
		new HostsFileStep(() => ctx.ResourceStaticIp, "conduent-resource", ctx.ResourceStaticIpInput()),
		new GitProxyStep(),
		new CreatePacFileStep(ctx),
		new WindowsProxyStep(ctx),
		new DevSettingsStep(),
		new StartupShortcutStep(SetupMode.Hub, ctx),
		new ShowTravelConfsStep(ctx),
	];

	private static List<ISetupStep> BuildTravel( SetupContext ctx ) =>
	[
		new InstallWireGuardStep(),
		new VerifyConfFileStep(ctx),
		new InstallTravelTunnelStep(ctx),
		new HostsFileStep(() => "10.0.0.1", "conduent-resource"),
		new GitProxyStep(),
		new CreatePacFileStep(ctx),
		new WindowsProxyStep(ctx),
		new DevSettingsStep(),
		new StartupShortcutStep(SetupMode.Travel, ctx),
	];

	private static List<ISetupStep> BuildResource( SetupContext ctx ) =>
	[
		new VpnProxyFirewallStep(ctx),
		new SyncFoldersStep(ctx),
		new StartupShortcutStep(SetupMode.Resource, ctx),
	];
}
