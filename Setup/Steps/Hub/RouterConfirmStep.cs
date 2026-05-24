namespace ConduentResourceMonitor.Setup.Steps.Hub;

public class RouterConfirmStep : ISetupStep
{
    private readonly SetupContext _ctx;

    public string Title => "Router Configuration";
    public bool RequiresElevation => false;
    public bool IsManual => true;
    public bool CanSkip => false;

    public string Description =>
        $"""
        Complete these steps on your router before continuing. These cannot be automated.

        1. Create a static lease for Hub
           IP: {(_ctx.HubStaticIp.Length > 0 ? _ctx.HubStaticIp : "<hub-lan-ip>")}
           (AmpliFi: Clients tab → Hub machine → Create Static Lease)

        2. Create a static lease for Resource
           IP: {(_ctx.ResourceStaticIp.Length > 0 ? _ctx.ResourceStaticIp : "<resource-lan-ip>")}

        3. Enable port forwarding: port 51820 (UDP) → Hub's static IP
           (AmpliFi: Settings → Port Forwarding → Add)

        Once done, click 'Mark Done' to continue.
        """;

    public RouterConfirmStep(SetupContext ctx) => _ctx = ctx;

    // Always re-shown — requires user confirmation every time
    public Task<bool> IsAlreadyCompleteAsync() => Task.FromResult(false);

    public Task<SetupStepResult> RunAsync(IProgress<string> _) =>
        Task.FromResult(new SetupStepResult(true, "Router configuration confirmed."));
}
