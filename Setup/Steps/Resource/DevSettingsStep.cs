namespace ConduentResourceMonitor.Setup.Steps.Resource;

public class DevSettingsStep : ISetupStep
{
    private const string SettingsPath = @"C:\BTR\GlobalConfiguration\CamelotSettings.Api.WebService.Proxy.json";

    public string Title => "Create Developer Settings File";
    public string Description =>
        $"""
        Creates the Camelot proxy settings file so the WebService.Proxy API routes
        debug requests through conduent-resource:8888.

        Path: {SettingsPath}
        """;
    public bool RequiresElevation => false;
    public bool IsManual => false;
    public bool CanSkip => false;

    public Task<bool> IsAlreadyCompleteAsync() =>
        Task.FromResult(File.Exists(SettingsPath));

    public Task<SetupStepResult> RunAsync(IProgress<string> progress)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, SettingsJson);
            progress.Report($"Created: {SettingsPath}");
            return Task.FromResult(new SetupStepResult(true, "Developer settings file created."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SetupStepResult(false, ex.Message));
        }
    }

    private const string SettingsJson = """
        {
          "TheKeep": {
            "Endpoints": {
              "ProxyServer": {
                "Url": "http://conduent-resource:8888",
                "BypassExpressions": [
                  "http://(?!HRSUAPPBA7003)|127\\.0\\.0\\.1|localhost|mymedicalshopper|api.telegram"
                ]
              }
            }
          }
        }
        """;
}
