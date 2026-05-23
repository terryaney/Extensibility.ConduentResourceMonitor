using System.Net.Sockets;

namespace ConduentResourceMonitor.Checks;

public class PortForwardCheck : ICheck
{
    private readonly string _host;
    private readonly int[] _ports;

    public string Name => "PortFwd";

    public PortForwardCheck(string host, params int[] ports)
    {
        _host = host;
        _ports = ports;
    }

    public async Task<CheckResult> RunAsync()
    {
        var failures = new List<int>();
        foreach (var port in _ports)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, port).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                failures.Add(port);
            }
        }

        return failures.Count == 0
            ? new CheckResult(Name, true, $"{string.Join(", ", _ports)} reachable")
            : new CheckResult(Name, false, $"Unreachable: {string.Join(", ", failures)}");
    }
}
