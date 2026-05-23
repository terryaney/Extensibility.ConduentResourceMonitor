using System.Net;

namespace ConduentResourceMonitor.Checks;

public class ProxyCheck : ICheck
{
    private readonly AppSettings _settings;
    private readonly string _checkName;
    private HttpClient? _client;
    private string? _lastProxyAddress;

    public string Name => _checkName;

    public ProxyCheck(string name, AppSettings settings)
    {
        _checkName = name;
        _settings = settings;
    }

    private HttpClient GetClient()
    {
        if (_client == null || _lastProxyAddress != _settings.ProxyAddress)
        {
            _client?.Dispose();
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://{_settings.ProxyAddress}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _lastProxyAddress = _settings.ProxyAddress;
        }
        return _client;
    }

    public async Task<CheckResult> RunAsync()
    {
        try
        {
            var response = await GetClient().GetAsync(_settings.CheckUrl);
            return new CheckResult(Name, true, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return new CheckResult(Name, false, msg);
        }
    }
}
