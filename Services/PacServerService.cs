using System.Net;
using System.Net.Sockets;

namespace ConduentResourceMonitor.Services;

public class PacServerService( AppSettings settings )
{
	private readonly AppSettings _settings = settings;
	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private Task? _acceptLoopTask;

	public string? LastError { get; private set; }

	public void Start()
	{
		if ( _listener != null ) return;
		try
		{
			_cts = new CancellationTokenSource();
			_listener = new TcpListener( IPAddress.Any, _settings.PacPort );
			_listener.Start();
			_acceptLoopTask = AcceptLoopAsync( _listener, _cts.Token );
			LastError = null;
		}
		catch ( Exception ex )
		{
			_listener = null;
			_cts = null;
			LastError = ex.Message; // TrayApp logs this immediately; PacServerCheck still surfaces ongoing failure each cycle
		}
	}

	public void Stop()
	{
		_cts?.Cancel();
		try { _listener?.Stop(); } catch { }
		try { _acceptLoopTask?.Wait( TimeSpan.FromSeconds( 2 ) ); } catch { }
		_listener = null;
		_cts?.Dispose();
		_cts = null;
	}

	public void Restart()
	{
		Stop();
		Start();
	}

	private async Task AcceptLoopAsync( TcpListener listener, CancellationToken token )
	{
		while ( !token.IsCancellationRequested )
		{
			TcpClient client;
			try { client = await listener.AcceptTcpClientAsync( token ); }
			catch ( OperationCanceledException ) { break; }
			catch ( ObjectDisposedException ) { break; } // Stop() calling listener.Stop()
			catch { continue; }                          // transient accept error, keep looping
			_ = HandleClientAsync( client, token );
		}
	}

	private async Task HandleClientAsync( TcpClient client, CancellationToken token )
	{
		using ( client )
		{
			try
			{
				var stream = client.GetStream();
				using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource( token );
				timeoutCts.CancelAfter( TimeSpan.FromSeconds( 5 ) );
				_ = await RawHttpUtil.ReadLineAsync( stream, timeoutCts.Token ); // request line, unused — every request gets the same file

				// Drain the remaining request headers up to the blank-line terminator before
				// responding and closing. HttpClient always sends at least a Host header — if
				// those bytes are left unread when the socket is disposed below, Windows performs
				// an abortive close (TCP RST) instead of a graceful FIN, which HttpClient surfaces
				// as "An existing connection was forcibly closed by the remote host."
				string? headerLine;
				do { headerLine = await RawHttpUtil.ReadLineAsync( stream, timeoutCts.Token ); }
				while ( !string.IsNullOrEmpty( headerLine ) );

				var path = Path.Combine( _settings.PacDirectory, _settings.PacFileName );
				var body = File.Exists( path ) ? await File.ReadAllBytesAsync( path, token ) : [];
				var status = body.Length > 0 ? "200 OK" : "404 Not Found";
				var header = $"HTTP/1.1 {status}\r\nContent-Type: application/x-ns-proxy-autoconfig\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
				await RawHttpUtil.WriteAsciiAsync( stream, header, token );
				if ( body.Length > 0 ) await stream.WriteAsync( body, token );
			}
			catch { /* isolated per connection */ }
		}
	}
}
