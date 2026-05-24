namespace ConduentResourceMonitor.Setup.Steps.Travel;

public class VerifyConfFileStep : ISetupStep
{
    private readonly SetupContext _ctx;

    public string Title => "Verify & Place Config File";
    public string Description =>
        $"""
        Confirms the WireGuard config file is in the correct directory.

        Config file: {_ctx.ConfFilePath}
        Target directory: {_ctx.ConfDirectory}

        If the file is not already in the target directory, it will be copied there.
        """;
    public bool RequiresElevation => false;
    public bool IsManual => false;
    public bool CanSkip => false;

    public VerifyConfFileStep(SetupContext ctx) => _ctx = ctx;

    public Task<bool> IsAlreadyCompleteAsync()
    {
        var targetPath = Path.Combine(_ctx.ConfDirectory, Path.GetFileName(_ctx.ConfFilePath));
        return Task.FromResult(File.Exists(targetPath));
    }

    public Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        try
        {
            var fileName = Path.GetFileName(_ctx.ConfFilePath);
            var targetPath = Path.Combine(_ctx.ConfDirectory, fileName);

            if (File.Exists(targetPath))
            {
                progress.Report($"Config file already in place: {targetPath}");
                return Task.FromResult(new SetupStepResult(true, "Config file already in target directory."));
            }

            Directory.CreateDirectory(_ctx.ConfDirectory);
            File.Copy(_ctx.ConfFilePath, targetPath);
            progress.Report($"Copied to: {targetPath}");
            return Task.FromResult(new SetupStepResult(true, "Config file copied to target directory."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SetupStepResult(false, ex.Message));
        }
    }
}
