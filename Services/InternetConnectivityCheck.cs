using System.Net.Sockets;

namespace ConduentResourceMonitor.Services;

public static class InternetConnectivityCheck
{
	private static readonly (string Host, int Port)[] Probes =
	[
		( "1.1.1.1", 443 ),
		( "8.8.8.8", 443 )
	];

	public static async Task<bool> IsOnlineAsync( TimeSpan timeout )
	{
		var tasks = Probes.Select( p => ProbeAsync( p.Host, p.Port, timeout ) );
		var results = await Task.WhenAll( tasks );
		return results.Any( ok => ok );
	}

	private static async Task<bool> ProbeAsync( string host, int port, TimeSpan timeout )
	{
		try
		{
			using var client = new TcpClient();
			await client.ConnectAsync( host, port ).WaitAsync( timeout );
			return true;
		}
		catch
		{
			return false;
		}
	}
}
