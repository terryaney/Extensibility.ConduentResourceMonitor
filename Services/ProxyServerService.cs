using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ConduentResourceMonitor.Services;

public class ProxyServerService
{
	public const int Port = 8888; // matches the existing firewall rule / PAC / dev-settings JSON

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private Task? _acceptLoopTask;
	private readonly ConcurrentDictionary<TcpClient, byte> _activeClients = new();

	public string? LastError { get; private set; }

	public void Start()
	{
		if ( _listener != null ) return;
		try
		{
			_cts = new CancellationTokenSource();
			_listener = new TcpListener( IPAddress.Any, Port );
			_listener.Start();
			_acceptLoopTask = AcceptLoopAsync( _listener, _cts.Token );
			LastError = null;
		}
		catch ( Exception ex )
		{
			_listener = null;
			_cts = null;
			LastError = ex.Message; // TrayApp logs this immediately; PortForwardCheck still surfaces ongoing failure each cycle
		}
	}

	public void Stop()
	{
		_cts?.Cancel();
		try { _listener?.Stop(); } catch { }                 // unblocks the pending accept
		foreach ( var c in _activeClients.Keys.ToArray() )     // snapshot — safe vs. concurrent self-removal
			try { c.Close(); } catch { }
		try { _acceptLoopTask?.Wait( TimeSpan.FromSeconds( 2 ) ); } catch { }
		_listener = null;
		_cts?.Dispose();
		_cts = null;
		_activeClients.Clear();
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

			_activeClients.TryAdd( client, 0 );
			_ = HandleClientAsync( client, token ).ContinueWith( t => _activeClients.TryRemove( client, out _ ) );
		}
	}

	private async Task HandleClientAsync( TcpClient client, CancellationToken token )
	{
		try
		{
			var stream = client.GetStream();
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource( token );
			timeoutCts.CancelAfter( TimeSpan.FromSeconds( 10 ) );

			var line = await RawHttpUtil.ReadLineAsync( stream, timeoutCts.Token );
			var parts = line?.Split( ' ', 3 );
			if ( parts is not { Length: 3 } )
			{
				await RawHttpUtil.WriteAsciiAsync( stream, "HTTP/1.1 400 Bad Request\r\n\r\n" );
				return;
			}

			var (method, target, version) = (parts[ 0 ], parts[ 1 ], parts[ 2 ]);
			string host; int port; Uri? httpUri = null;

			if ( method == "CONNECT" )
			{
				var idx = target.LastIndexOf( ':' );
				if ( idx < 0 || !int.TryParse( target[ ( idx + 1 ).. ], out port ) )
				{
					await RawHttpUtil.WriteAsciiAsync( stream, "HTTP/1.1 400 Bad Request\r\n\r\n" );
					return;
				}
				host = target[ ..idx ];

				// A real CONNECT request still has a Host header and a blank-line terminator
				// following the request line. Drain them here — if we don't, those bytes leak
				// into the raw relay below and corrupt the TLS handshake the client is about to
				// start. Contents are unused; CONNECT carries nothing else the tunnel needs once
				// host:port is known.
				string? headerLine;
				do { headerLine = await RawHttpUtil.ReadLineAsync( stream, timeoutCts.Token ); }
				while ( !string.IsNullOrEmpty( headerLine ) );
			}
			else if ( Uri.TryCreate( target, UriKind.Absolute, out httpUri ) && httpUri.Scheme == "http" )
			{
				host = httpUri.Host;
				port = httpUri.IsDefaultPort ? 80 : httpUri.Port;
			}
			else
			{
				await RawHttpUtil.WriteAsciiAsync( stream, "HTTP/1.1 400 Bad Request\r\n\r\n" );
				return;
			}

			using var targetClient = new TcpClient();
			try { await targetClient.ConnectAsync( host, port ).WaitAsync( TimeSpan.FromSeconds( 10 ), token ); }
			catch { await RawHttpUtil.WriteAsciiAsync( stream, "HTTP/1.1 502 Bad Gateway\r\n\r\n" ); return; }

			var targetStream = targetClient.GetStream();

			if ( method == "CONNECT" )
			{
				await RawHttpUtil.WriteAsciiAsync( stream, "HTTP/1.1 200 Connection Established\r\n\r\n" );
			}
			else
			{
				// Rewrite absolute-form ("GET http://host/path HTTP/1.1") to origin-form
				// ("GET /path HTTP/1.1") before forwarding. Origin servers commonly reject or
				// mishandle absolute-form request lines — only a proxy is required to accept
				// that form (RFC 7230 §5.3.2). Headers/body after this line are untouched — we
				// never read them, so they flow through RelayAsync exactly as the client sent them.
				var originForm = $"{method} {httpUri!.PathAndQuery} {version}\r\n";
				await RawHttpUtil.WriteAsciiAsync( targetStream, originForm );
			}

			await RelayAsync( stream, targetStream, token );
		}
		catch { /* isolated per connection — never let a bad connection affect others */ }
		finally { client.Dispose(); }
	}

	private static async Task RelayAsync( NetworkStream a, NetworkStream b, CancellationToken token )
	{
		try
		{
			var t1 = a.CopyToAsync( b, 81920, token );
			var t2 = b.CopyToAsync( a, 81920, token );
			await Task.WhenAny( t1, t2 );
		}
		catch { }
	}
}
