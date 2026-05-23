namespace ConduentResourceMonitor.Checks;

public class PacServerCheck : ICheck
{
    private readonly AppSettings _settings;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string Name => "PAC";

    public PacServerCheck(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<CheckResult> RunAsync()
    {
        try
        {
            var response = await _client.GetAsync($"http://localhost:{_settings.PacPort}/conduent-resource.pac");
            return new CheckResult(Name, true, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new CheckResult(Name, false, ex.InnerException?.Message ?? ex.Message);
        }
    }
}
