using ConduentResourceMonitor.Setup.Steps.Hub;
using ConduentResourceMonitor.Setup.Steps.Resource;
using ConduentResourceMonitor.Setup.Steps.Shared;
using ConduentResourceMonitor.Setup.Steps.Travel;

namespace ConduentResourceMonitor.Setup;

internal static class StepFactory
{
    public static List<ISetupStep> Build(SetupMode mode, SetupContext ctx) => mode switch
    {
        SetupMode.Hub => BuildHub(ctx),
        SetupMode.Travel => BuildTravel(ctx),
        SetupMode.Resource => BuildResource(ctx),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static List<ISetupStep> BuildHub(SetupContext ctx)
    {
        var steps = new List<ISetupStep> { new RouterConfirmStep(ctx) };
        if (!ctx.SkipWireGuard)
        {
            steps.Add(new InstallWireGuardStep());
            steps.Add(new GenerateAllKeysStep(ctx));
            steps.Add(new InstallHubTunnelStep(ctx));
        }
        steps.AddRange([
            new FirewallRulesStep(),
            new PortProxyRulesStep(ctx),
            new HostsFileStep(ctx.ResourceStaticIp, "conduent-resource"),
            new GitProxyStep(),
            new InstallPythonStep(),
            new CreatePacFileStep(ctx.ConfDirectory),
            new WindowsProxyStep(ctx),
            new StartupShortcutStep(SetupMode.Hub, ctx),
            new ShowTravelConfsStep(ctx),
        ]);
        return steps;
    }

    private static List<ISetupStep> BuildTravel(SetupContext ctx) =>
    [
        new InstallWireGuardStep(canSkip: false),
        new VerifyConfFileStep(ctx),
        new InstallTravelTunnelStep(ctx),
        new HostsFileStep("10.0.0.1", "conduent-resource"),
        new GitProxyStep(),
        new InstallPythonStep(),
        new CreatePacFileStep(ctx.ConfDirectory),
        new WindowsProxyStep(ctx),
        new StartupShortcutStep(SetupMode.Travel, ctx),
    ];

    private static List<ISetupStep> BuildResource(SetupContext ctx) =>
    [
        new InstallPythonStep(),
        new InstallPproxyStep(),
        new PproxyFirewallStep(),
        new TerminalProfileStep(),
        new StartupShortcutStep(SetupMode.Resource, ctx),
        new DevSettingsStep(),
    ];
}
