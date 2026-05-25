using System.Net.Sockets;

namespace ConduentResourceMonitor.Checks;

public class PortForwardCheck( string name, string host, params int[] ports ) : ICheck
{
	public string Name => name;

	public async Task<CheckResult> RunAsync()
	{
		var failures = new List<int>();
		foreach ( var port in ports )
		{
			try
			{
				using var client = new TcpClient();
				await client.ConnectAsync( host, port ).WaitAsync( TimeSpan.FromSeconds( 5 ) );
			}
			catch
			{
				failures.Add( port );
			}
		}

		return failures.Count == 0
			? new CheckResult( Name, true, $"{string.Join( ", ", ports )} reachable" )
			: new CheckResult( Name, false, $"Unreachable: {string.Join( ", ", failures )}" );
	}
}