# Native `AppMode.Resource` + native PAC server — remove Python entirely

## Context

Today two things shell out to Python:
1. The Resource machine exposes its corporate VPN as an HTTP proxy on port 8888 using
   `pproxy` (a Python package), launched by hand from a Windows Terminal profile.
   `--setup Resource` automates installing Python, `pip install pproxy`, that Terminal
   profile, a startup shortcut, and a firewall rule. This is the only leg of the 3-machine
   setup (Hub, Travel, Resource) that isn't a real `AppMode` of the tray monitor — no
   health check, no auto-repair, no tray icon on Resource.
   `Repairs/ResourceVpnRepair.cs` is a MessageBox telling a Hub/Travel operator to "remote
   into Resource and check" precisely because Resource can't self-report today.
2. `Services/PacServerService.cs` runs `python -m http.server {port}` as a child process on
   **every mode** (Hub and Travel today) to serve `conduent-resource.pac` to browsers.

Goal: add a real `AppMode.Resource` that runs a native C# HTTP forward proxy (CONNECT +
plain-HTTP) in-process, monitored/repairable exactly like Hub/Travel's existing checks —
**and** rewrite `PacServerService` to serve the PAC file natively too, so Python is not a
dependency of this application anywhere, for any mode. `Setup/Steps/Shared/InstallPythonStep.cs`
and every setup-wizard reference to installing Python/pproxy/pip go away entirely.

Design constraints carried through the whole plan:
- Full replacement, no fallback flag — simplest correct option, matches what was asked.
- Ports stay hardcoded constants (8888 already hardcoded in the firewall rule, the PAC
  file, and the dev-settings JSON; PAC port already defaults via `AppSettings.PacPort`) —
  no new configurable "listen port" setting beyond what already exists.
- Reuse existing patterns wherever they fit (`PacServerService`'s Start/Stop/Restart shape,
  `PortForwardCheck` for listener liveness, `IRepair`'s name-matched-to-check convention)
  rather than inventing new abstractions. The proxy and the rewritten PAC server share a
  small helper for raw-socket line reading (see §3) rather than duplicating that logic.

---

## 1. `Options.cs`

- `enum AppMode { Hub, Travel }` → `enum AppMode { Hub, Travel, Resource }` (line 5).
- Update the `--mode` option's `HelpText` (line 11) from `"Hub or Travel. If omitted..."`
  to `"Hub, Travel, or Resource. If omitted..."`.

## 2. `AppSettings.cs`

- Add `public const string DefaultResourceProxyAddress = "localhost:8888";` next to the
  existing `DefaultProxyAddress = "conduent-resource:8888"` (line 8). Resource's own
  `ProxyCheck` must hit its own local listener, not the `conduent-resource` hosts-file
  name (which only exists on Hub/Travel, written by `HostsFileStep` — Resource never gets
  that hosts entry).
- In `ApplyOverrides` (line 63), when the mode is transitioning *into* Resource and
  `ProxyAddress` is still sitting at the untouched shared default, rewrite it once:
  ```csharp
  public void ApplyOverrides( Options options )
  {
      if ( options.Mode.HasValue )
      {
          var newModeStr = options.Mode.Value.ToString();
          if ( newModeStr != Mode && options.Mode.Value == AppMode.Resource && ProxyAddress == DefaultProxyAddress )
              ProxyAddress = DefaultResourceProxyAddress;
          Mode = newModeStr;
      }
      if ( options.CheckUrl != null ) CheckUrl = options.CheckUrl;
      ... // rest unchanged
  }
  ```
  Fires once, exactly on the transition (the Resource startup shortcut always passes
  `--mode Resource`, so this runs every launch, but `newModeStr != Mode` means it only
  overwrites on the *first* launch after switching to Resource — never stomps a value the
  operator later hand-edits). No change to `Validate()` — it already only requires
  `ProxyAddress` non-empty, which Resource satisfies.
- Once `Repairs/ResourceVpnRepair.cs` (§9) and `Setup/Steps/Shared/StartupShortcutStep.cs`
  (§13) no longer reference `ResourceProviderTerminalProfileName` (line 10), delete that
  constant — confirmed via grep it's used only in those two files plus
  `Setup/Steps/Resource/TerminalProfileStep.cs`, which is deleted outright in §12.
- **CODE REVIEW FIX (Major #3):** `Validate()` (line 75) currently requires `TunnelName`
  non-empty, `PacDirectory` to exist, and `PacPort` in range — *unconditionally*, for every
  mode. Once §14 hides those fields from Resource's Settings UI, an operator with no way to
  see/edit them could get permanently blocked if the shared `settings.json` happens to carry
  a stale/invalid value from an earlier Hub/Travel run on the same box (a real scenario in
  dev/test — this plan's own verification steps run Hub then Resource against the same
  settings file). Wrap the three mode-irrelevant checks:
  ```csharp
  if ( AppMode != AppMode.Resource )
  {
      if ( string.IsNullOrWhiteSpace( TunnelName ) )
          errors.Add( "Tunnel Name is required" );

      if ( string.IsNullOrWhiteSpace( PacDirectory ) )
          errors.Add( "PAC Directory is required" );
      else if ( !Directory.Exists( PacDirectory ) )
          errors.Add( $"PAC Directory does not exist: '{PacDirectory}'" );

      if ( PacPort is < 1 or > 65535 )
          errors.Add( $"PAC Port must be between 1 and 65535 (got {PacPort})" );
  }
  ```
  `CheckUrl`, `ProxyAddress`, `CheckIntervalSeconds`, `NotifyTimeoutMs` validation stays
  unconditional — all four apply to every mode including Resource.

## 3. `Services/RawHttpUtil.cs` — NEW (shared by both native services)

Both the proxy and the rewritten PAC server need to read one HTTP request line off a raw
`NetworkStream` byte-by-byte (a buffered/`StreamReader` read would over-consume bytes past
the line — TLS `ClientHello` for the proxy, nothing-follows for the PAC server, but the
same correctness concern applies) and write a plain ASCII response. Factor this into one
small internal static helper instead of duplicating it in both services:

```csharp
namespace ConduentResourceMonitor.Services;

internal static class RawHttpUtil
{
    public static async Task<string?> ReadLineAsync( NetworkStream stream, CancellationToken token )
    {
        var buffer = new List<byte>();
        var b = new byte[1];
        while ( true )
        {
            var read = await stream.ReadAsync( b, token );
            if ( read == 0 ) return buffer.Count == 0 ? null : Encoding.ASCII.GetString( [.. buffer] );
            if ( b[0] == '\n' ) break;
            if ( b[0] != '\r' ) buffer.Add( b[0] );
        }
        return Encoding.ASCII.GetString( [.. buffer] );
    }

    public static Task WriteAsciiAsync( NetworkStream stream, string text, CancellationToken token = default ) =>
        stream.WriteAsync( Encoding.ASCII.GetBytes( text ), token ).AsTask();
}
```

## 4. `Services/ProxyServerService.cs` — NEW

Mirrors `PacServerService`'s current `Start()`/`Stop()`/`Restart()` shape, but owns an
in-process `TcpListener` + accept loop instead of a child `Process`.

```csharp
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
            LastError = ex.Message; // TrayApp logs this immediately (§10); PortForwardCheck still surfaces ongoing failure each cycle
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

    public void Restart() { Stop(); Start(); }

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
            _ = HandleClientAsync( client, token ).ContinueWith( _ => _activeClients.TryRemove( client, out _ ) );
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

            var (method, target, version) = (parts[0], parts[1], parts[2]);
            string host; int port; Uri? httpUri = null;

            if ( method == "CONNECT" )
            {
                var idx = target.LastIndexOf( ':' );
                if ( idx < 0 || !int.TryParse( target[(idx + 1)..], out port ) )
                {
                    await RawHttpUtil.WriteAsciiAsync( stream, "HTTP/1.1 400 Bad Request\r\n\r\n" );
                    return;
                }
                host = target[..idx];

                // CODE REVIEW FIX (Critical #1): a real CONNECT request still has a Host header
                // and a blank-line terminator following the request line. Drain them here — if we
                // don't, those bytes leak into the raw relay below and corrupt the TLS handshake
                // the client is about to start. Contents are unused; CONNECT carries nothing else
                // the tunnel needs once host:port is known.
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
                // CODE REVIEW FIX (Major #5): rewrite absolute-form ("GET http://host/path HTTP/1.1")
                // to origin-form ("GET /path HTTP/1.1") before forwarding. Origin servers commonly
                // reject or mishandle absolute-form request lines — only a proxy is required to
                // accept that form (RFC 7230 §5.3.2). Headers/body after this line are untouched —
                // we never read them, so they flow through RelayAsync exactly as the client sent them.
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
```

Key points for the implementer:
- `_activeClients` is a `ConcurrentDictionary<TcpClient, byte>` used purely as a
  thread-safe set (lock-free add from the accept loop, lock-free remove from arbitrary
  handler-completion continuations) — no explicit locking needed, unlike `TrayApp`'s
  `_repairsInFlight` (a plain `HashSet` that needs its own `lock`).
- `Stop()` is synchronous, matching the existing `PacServerService.Stop()` signature
  exactly, since every call site (`TrayApp` constructor, `Shutdown()`, `Dispose()`,
  `IRepair.ExecuteAsync` which returns `Task.CompletedTask` today) expects sync
  fire-and-forget, not `async Task`. The bounded 2-second `.Wait()` inside is a deliberate,
  small tradeoff to keep that parity rather than threading `async Task Stop()` through
  four call sites.
- Each connection is fully isolated in its own `try/catch` — one bad/slow connection can
  never take down the accept loop or any other in-flight relay.
- CONNECT covers the overwhelming majority of real traffic (everything in the PAC file's
  domain list and the TFS git proxy are `https://`); the plain-HTTP absolute-URI branch
  exists for full pproxy parity but is the minority path.
- Also add `public string? LastError { get; private set; }`, set in `Start()`'s `catch`
  (`LastError = ex.Message`) and cleared to `null` on a successful start — see §10 for how
  `TrayApp` surfaces this immediately instead of waiting for the next check cycle.

## 5. `Services/PacServerService.cs` — REWRITE (used by Hub and Travel)

Same public shape (`Start()`/`Stop()`/`Restart()`, constructed with `AppSettings`) so
`TrayApp` needs zero changes to how it's called — only the internals change from
`Process.Start("python", "-m http.server ...")` to an in-process `TcpListener` that always
answers with the PAC file's bytes, structurally identical to `ProxyServerService`'s
accept-loop shape but simpler (no relay — request in, one canned response out, close):

```csharp
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
            LastError = ex.Message; // TrayApp logs this immediately (§10); PacServerCheck still surfaces ongoing failure each cycle
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

    public void Restart() { Stop(); Start(); }

    private async Task AcceptLoopAsync( TcpListener listener, CancellationToken token )
    {
        // CODE REVIEW FIX (Minor #6): was a single catch-all `{ break; }`, which would kill the
        // whole PAC server permanently on any transient accept error, not just clean shutdown.
        // Matches ProxyServerService's more careful split.
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

                // POST-IMPLEMENTATION FIX (found during Hub verification): drain the remaining
                // request headers up to the blank-line terminator before responding and closing.
                // HttpClient always sends at least a Host header — if those bytes are left unread
                // when the socket is disposed below, Windows performs an abortive close (TCP RST)
                // instead of a graceful FIN, which HttpClient surfaces as "An existing connection
                // was forcibly closed by the remote host." Same fix class as CONNECT header
                // draining in ProxyServerService (§4) — missed applying it here originally.
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
```

`Checks/PacServerCheck.cs` needs **no changes** — it already does a plain `HttpClient.GetAsync(settings.PacUrl)` and only cares about getting back a valid HTTP response, which this now produces natively. `Repairs/PacServerRepair.cs` needs **no changes** either — it just calls `PacServerService.Restart()`, same signature as before.

## 6. `Checks/` — no new file for the proxy listener check

Reuse `Checks/PortForwardCheck.cs` as-is: `new PortForwardCheck("VPN Proxy", "localhost",
ProxyServerService.Port)`. It already does exactly a TCP-connect check parameterized by
name/host/ports — no need for a bespoke `ProxyListenerCheck`.

`Checks/ProxyCheck.cs` is reused unmodified too — its behavior is entirely driven by
`settings.ProxyUrl`, which resolves correctly to `http://localhost:8888` once §2's default
lands.

## 7. `Repairs/ProxyServerRepair.cs` — NEW

Directly mirrors `Repairs/PacServerRepair.cs`:

```csharp
using ConduentResourceMonitor.Services;
using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class ProxyServerRepair( ProxyServerService service, ICheck check ) : IRepair
{
    private readonly ProxyServerService _service = service;

    public string Label => $"Restart {TargetCheckName}";
    public string TargetCheckName => check.Name; // "VPN Proxy"
    public bool RequiresElevation => false;        // eligible for auto-repair

    public Task ExecuteAsync( Action<string>? logLine = null )
    {
        logLine?.Invoke( "Restarting native proxy listener." );
        _service.Restart();
        return Task.CompletedTask;
    }
}
```

## 8. `Repairs/LocalVpnRepair.cs` — NEW

Same shape as `Repairs/ResourceVpnRepair.cs` but with local wording (you're already on the
Resource machine, "remote into Resource" makes no sense here). Matched to the same check
name (`"Conduent VPN"`) so it slots into `TrayApp`'s existing name-based matching with zero
changes to that matching logic:

```csharp
using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class LocalVpnRepair( ICheck check ) : IRepair
{
    public string Label => "Check Conduent VPN";
    public string TargetCheckName => check.Name; // "Conduent VPN"
    public bool RequiresElevation => true;         // manual only — never auto-popup

    public Task ExecuteAsync( Action<string>? logLine = null )
    {
        logLine?.Invoke( "Conduent VPN repair is manual and requires user action." );
        MessageBox.Show(
            "Global Connect VPN appears disconnected on this machine. Reconnect the VPN and confirm the corporate URL is reachable — this check will pass on the next cycle.",
            "Fix: Check Conduent VPN",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information );
        return Task.CompletedTask;
    }
}
```

`Repairs/ResourceVpnRepair.cs` is untouched in behavior (still used by Hub/Travel), only
its message text changes — see §9.

## 9. `Repairs/ResourceVpnRepair.cs`

Line 15 currently reads:
```csharp
$"Remote to the Resource machine and ensure VPN is enabled and that the '{AppSettings.ResourceProviderTerminalProfileName}' terminal profile is running."
```
Reword to drop the now-nonexistent terminal-profile reference:
```csharp
"Remote to the Resource machine and ensure Global Connect VPN is connected and the native proxy is running (check the Resource machine's tray icon)."
```

## 10. `TrayApp.cs`

**Fields** (lines 10-21): change `private readonly PacServerService _pacServer;` to
`private readonly PacServerService? _pacServer;` and add
`private readonly ProxyServerService? _proxyServer;` — only one is populated depending on
mode.

**Constructor** (replace lines 33-34):
```csharp
if ( _mode == AppMode.Resource )
{
    _proxyServer = new ProxyServerService();
    _proxyServer.Start();
    if ( _proxyServer.LastError != null )
        _logForm.AppendLine( $"[{DateTime.Now:HH:mm:ss}] VPN Proxy failed to start: {_proxyServer.LastError}" );
}
else
{
    _pacServer = new PacServerService( settings );
    _pacServer.Start();
    if ( _pacServer.LastError != null )
        _logForm.AppendLine( $"[{DateTime.Now:HH:mm:ss}] PAC Web Server failed to start: {_pacServer.LastError}" );
}
```
Resource never needs the PAC web server (no PAC consumers on that box). The `LastError`
logging (CODE REVIEW FIX, Major #4) gives an immediate, specific reason in the log window
the moment `Start()` fails (e.g. "port already in use") instead of waiting up to
`CheckIntervalSeconds` for `PortForwardCheck`/`PacServerCheck` to report a generic
"Unreachable" with no root cause. `_logForm` already exists at this point in the
constructor (created at line 31, before this block).

**`BuildChecks`** (lines 62-73) → real 3-way `switch`, keeping Hub/Travel's current
behavior identical and adding Resource's own branch (no `WireGuardCheck` — Resource is not
a WireGuard peer; no `PacServerCheck` — Resource never serves a `.pac` file):
```csharp
private static IReadOnlyList<ICheck> BuildChecks( AppMode mode, AppSettings settings ) => mode switch
{
    AppMode.Hub => [
        new ProxyCheck( settings ),
        new PortForwardCheck( "Port Proxy / Forwarding", "localhost", 8888, 13389 ),
        new PacServerCheck( settings ),
        new WireGuardCheck( settings )
    ],
    AppMode.Travel => [
        new ProxyCheck( settings ),
        new PortForwardCheck( "Resource RDP", "conduent-resource", 13389 ),
        new PacServerCheck( settings ),
        new WireGuardCheck( settings )
    ],
    AppMode.Resource => [
        new ProxyCheck( settings ),
        new PortForwardCheck( "VPN Proxy", "localhost", ProxyServerService.Port )
    ],
    _ => throw new ArgumentOutOfRangeException( nameof( mode ) )
};
```
(This also fixes a latent bug the current ternary has: today anything that isn't
`AppMode.Hub` silently falls into Travel's check shape. The explicit switch keeps Hub and
Travel byte-for-byte identical to today while making Resource's branch self-contained.)

**`BuildRepairs`** (lines 75-88) → real `switch`, mirroring `BuildChecks`'s mode split:
```csharp
private List<IRepair> BuildRepairs( AppMode mode, AppSettings settings, IReadOnlyList<ICheck> checks )
{
    var proxyCheck = checks.First( i => i is ProxyCheck );
    var repairs = new List<IRepair>();

    switch ( mode )
    {
        case AppMode.Hub:
            repairs.Add( new ResourceVpnRepair( proxyCheck ) );
            repairs.Add( new PortProxyRepair( settings, checks.First( i => i is PortForwardCheck ) ) );
            repairs.Add( new WireGuardRepair( settings, checks.First( i => i is WireGuardCheck ) ) );
            repairs.Add( new PacServerRepair( _pacServer!, checks.First( i => i is PacServerCheck ) ) );
            break;
        case AppMode.Travel:
            repairs.Add( new ResourceVpnRepair( proxyCheck ) );
            repairs.Add( new WireGuardRepair( settings, checks.First( i => i is WireGuardCheck ) ) );
            repairs.Add( new PacServerRepair( _pacServer!, checks.First( i => i is PacServerCheck ) ) );
            break;
        case AppMode.Resource:
            repairs.Add( new LocalVpnRepair( proxyCheck ) );
            repairs.Add( new ProxyServerRepair( _proxyServer!, checks.First( i => i is PortForwardCheck ) ) );
            break;
    }
    return repairs;
}
```

**`RebuildMenu`'s Settings handler** (lines 164-177): change `_pacServer.Restart();` to
`_pacServer?.Restart();` (Resource never has PAC fields visible/changed per §14, so this
branch won't fire for Resource anyway, but the null-guard is required now that the field
is nullable).

**`Shutdown()`** (line 189) and **`Dispose(bool)`** (line 269): change `_pacServer.Stop();`
to `_pacServer?.Stop(); _proxyServer?.Stop();`.

Everything else — `OnResultsUpdated`, `OnCheckFailed`, `RebuildMenu`'s fix-item building,
the repair-queueing/dedup machinery, the Hub-only startup port-proxy repair block (already
gated on `_mode == AppMode.Hub`) — needs **no changes**; it all matches checks/repairs by
`CheckResult.Name`/`IRepair.TargetCheckName` strings and works automatically for Resource's
new checks and repairs.

## 11. `Program.cs` — `WriteHelp` only

Add a third branch between the existing Travel branch (ends line 287) and the generic
`else` (line 288):
```csharp
else if ( mode == AppMode.Resource )
{
    Console.WriteLine( "Usage:" );
    Console.WriteLine( "  ConduentResourceMonitor.exe --mode Resource [options]" );
    Console.WriteLine();
    Console.WriteLine( "What Resource Monitors:" );
    Console.WriteLine( $"  Conduent VPN      HTTP to internal Conduent URL via localhost:{Services.ProxyServerService.Port}" );
    Console.WriteLine( $"  VPN Proxy  TCP listener on localhost:{Services.ProxyServerService.Port} (replaces pproxy)" );
    Console.WriteLine();
    Console.WriteLine( "Options:" );
    OptFromOption( nameof( Options.CheckUrl ), defaultCheckUrl );
    OptFromOption( nameof( Options.CheckIntervalSeconds ), defaultCheckInterval );
    OptFromOption( nameof( Options.NotifyTimeoutMs ), defaultNotifyTimeout );
    OptFromOption( nameof( Options.ShowLog ) );
    Console.WriteLine();
    Console.WriteLine( "Tip: Run -? without --mode for full help including setup options." );
}
```
(Resource intentionally omits `TunnelName`/`PacDirectory`/`PacPort` — it uses neither
WireGuard nor PAC serving, matching §14's Settings-UI hiding.)

Update the generic block's usage lines (240-294):
- `"  ConduentResourceMonitor.exe --mode Hub|Travel [options]"` → add `|Resource`.
- `"  ConduentResourceMonitor.exe -? [--mode Hub|Travel]"` → add `|Resource`.
- `"Tip: Run -? --mode Hub or -? --mode Travel for mode-specific help."` → mention Resource.

`RunMonitor`/`Run`/`ParseRequestedMode` need no changes — already generic over
`AppMode?`/`SetupMode`. `RunSetup`'s preflight-skip logic (line 154) does need one change —
see §12b, which now shows the preflight form for Resource too (to collect `HubStaticIp`).

## 12. `Setup/StepFactory.cs`

**`BuildResource`** (lines 54-62) — replace entirely:
```csharp
private static List<ISetupStep> BuildResource( SetupContext ctx ) =>
[
    new PproxyFirewallStep(),                  // still opens 8888 inbound — process-agnostic, keep as-is
    new StartupShortcutStep( SetupMode.Resource, ctx ),
    new DevSettingsStep(),                      // unaffected — unrelated to who's listening on 8888
];
```
Drop `InstallPythonStep`, `InstallPproxyStep`, `TerminalProfileStep` from this list.

**`BuildHub`** (lines 18-39) and **`BuildTravel`** (lines 41-52) — remove the
`new InstallPythonStep(),` line from each (both currently include it for the old
`python -m http.server` PAC dependency, which no longer exists after §5).

Delete these files outright — each becomes fully orphaned (no other reference anywhere in
the repo, confirmed via grep):
- `Setup/Steps/Resource/InstallPproxyStep.cs`
- `Setup/Steps/Resource/TerminalProfileStep.cs`
- `Setup/Steps/Shared/InstallPythonStep.cs`

Leave `PproxyFirewallStep.cs`'s class name and the `netsh` rule name (`"pproxy"`) as-is —
renaming an already-deployed firewall rule name non-idempotently on existing Resource
machines is more risk than the cosmetic benefit is worth.

## 12b. Firewall scoping — `SetupContext.cs`, `SetupPreflightForm.cs`, `PproxyFirewallStep.cs`, `Program.cs`

**CODE REVIEW FIX (Critical #2):** today's firewall rule
(`netsh advfirewall firewall add rule name=pproxy protocol=TCP dir=in localport=8888
action=allow`) has no `profile=`/`remoteip=` scoping — any device that can reach Resource on
port 8888 can use it as an open relay into the corporate VPN, and since this is now
first-party code we own (not just pproxy's default behavior we inherited), it's worth
tightening while we're already touching this step. Per the traffic-path analysis earlier in
this project's history, Travel never talks to Resource directly — Travel's traffic reaches
`conduent-resource:8888` via the WireGuard tunnel to Hub, and Hub's own `netsh portproxy`
rule forwards it onward, so **every** TCP connection Resource ever sees on 8888 originates
from Hub's LAN IP, whether it's Hub's own use or something forwarded on Travel's behalf.
The rule only needs to allow that one source.

Collecting Hub's IP as a new Resource-setup input (mirrors the existing `ResourceStaticIp`
field Hub's own wizard already collects, just the mirror image):
1. `Setup/SetupContext.cs` (line 9, next to `ResourceStaticIp`): add
   `public string HubStaticIp { get; set; } = "";`.
2. `Setup/SetupPreflightForm.cs`: this form already switches on `SetupMode` to build
   mode-specific content (`BuildHubContent()`/`BuildTravelContent()`, `Size` per mode,
   validation/save methods gated by `if (_mode == SetupMode.Hub) ... else if (_mode ==
   SetupMode.Travel) ...` — see lines 66-67, 77-78, 228-261). Add a fourth branch,
   `BuildResourceContent()`, with a single row: "Hub Static LAN IP" (`AddRow` helper
   already used for `ResourceStaticIp` at line 98, e.g. placeholder `"e.g. 192.168.158.2"`),
   plus matching `SetupMode.Resource` cases in the validation and save switches (require
   non-empty, save into `ctx.HubStaticIp`).
3. `Program.cs` `RunSetup` (line 154): currently `else if ( mode != SetupMode.Resource )`
   skips the preflight form entirely for Resource — change to `else` so Resource shows it
   too (Hub always shows it today; this makes Resource consistent with Hub rather than
   with Travel's conditional-skip behavior, since Resource's `HubStaticIp` field has no
   "already configured" auto-detection the way Travel's conf-file does).
4. `PproxyFirewallStep.cs`: change the constructor to accept `SetupContext ctx`. Change the
   elevated command list from a single `add rule` to **delete-then-add-then-verify**, per
   confirmed guidance — this is better than a bare `add`, because it makes the rule
   self-correcting on any re-run (e.g. if Hub's IP changes and the operator re-runs Resource
   setup) instead of leaving a stale `remoteip` behind:
   ```csharp
   var commands = new List<ElevatedCommand>
   {
       new()
       {
           FileName = "netsh",
           Arguments = ["advfirewall", "firewall", "delete", "rule", "name=pproxy"],
           Description = "Removing any existing pproxy firewall rule"
       },
       new()
       {
           FileName = "netsh",
           Arguments = ["advfirewall", "firewall", "add", "rule", "name=pproxy", "dir=in", "action=allow", "protocol=TCP", "localport=8888", "profile=private", $"remoteip={ctx.HubStaticIp}"],
           Description = "Creating scoped pproxy firewall rule"
       },
       new()
       {
           FileName = "netsh",
           Arguments = ["advfirewall", "firewall", "show", "rule", "name=pproxy", "verbose"],
           Description = "Verifying pproxy firewall rule"
       }
   };
   ```
   The `delete` is expected to no-op (non-zero exit / "No rules match" output) on a truly
   fresh machine where the rule has never existed — `ProcessHelper.RunElevatedCommandsAsync`
   already tolerates non-fatal command output today (`InstallPproxyStep`'s own idempotency
   check works the same way, treating a failed lookup as "not yet done" rather than a hard
   error), so no special-casing is needed here.
5. `Setup/StepFactory.cs` `BuildResource` (§12 above): change `new PproxyFirewallStep()` to
   `new PproxyFirewallStep( ctx )`.

**Confirmed scope, per this project's earlier traffic-path analysis:** `remoteip` only ever
needs Hub's single static LAN IP — Travel never talks to Resource directly. Travel's
traffic reaches `conduent-resource:8888` via the WireGuard tunnel to Hub, and Hub's own
`netsh portproxy` rule forwards it onward, so every TCP connection Resource ever sees on
8888 originates from Hub's LAN IP, whether it's Hub's own use or something forwarded on
Travel's behalf. No Travel IPs/subnets need to be in the rule.

The delete-then-add sequence resolves the "stale `remoteip` on IP change" risk noted in an
earlier draft of this plan, as long as the operator re-runs `--setup Resource` after any
Hub IP change (still no automatic drift *detection* — `IsAlreadyCompleteAsync()` only
checks the rule exists by name, so the wizard won't prompt a re-run on its own — but the
step is now correct and safe to re-run manually at any time, which it wasn't before).

## 13. `Setup/Steps/Shared/StartupShortcutStep.cs`

Collapse the current `if (_mode == SetupMode.Resource) { wt.exe... } else { ... }` special
case (lines 42-57) into one uniform 3-way branch:
```csharp
var exePath = Path.Combine( AppContext.BaseDirectory, "ConduentResourceMonitor.exe" );
var args = _mode switch
{
    SetupMode.Hub => "--mode Hub --repair-on-start",
    SetupMode.Travel => "--mode Travel",
    SetupMode.Resource => "--mode Resource",
    _ => ""
};
lnk.TargetPath = exePath;
lnk.Arguments = args;
lnk.WorkingDirectory = AppContext.BaseDirectory;
lnk.IconLocation = @"C:\BTR\Extensibility\PowerShell\Icons\vpn.png";
```
Update `Description` (line 17) to `"Creates a startup shortcut that launches the Resource
Monitor in Resource mode."` and `ShortcutName` (line 25) to `"Conduent Resource Monitor -
Resource.lnk"` (dropping the old `$"{AppSettings.ResourceProviderTerminalProfileName}.lnk"`
naming, which no longer applies once there's no Terminal profile).

## 14. `SettingsForm.cs`

**Mode combo** (line 80): `_modeCombo.Items.AddRange("Hub", "Travel")` →
`_modeCombo.Items.AddRange("Hub", "Travel", "Resource")`.

**Conditional row visibility** for Tunnel Name / PAC Directory / PAC Port (meaningless for
Resource — no WireGuard, no PAC serving; showing them would be misleading). Add a small
`Dictionary<string, Label> _labels` populated alongside `_fields` inside `AddField`, then:
```csharp
private void UpdateFieldVisibility()
{
    var isResource = _modeCombo.SelectedItem as string == "Resource";
    foreach ( var key in new[] { nameof(AppSettings.TunnelName), nameof(AppSettings.PacDirectory), nameof(AppSettings.PacPort) } )
    {
        _fields[key].Visible = !isResource;
        _labels[key].Visible = !isResource;
    }
}
```
Call once at the end of the constructor (after `_modeCombo.SelectedItem = settings.Mode`)
and again inside `OnModeChanged`. The `TableLayoutPanel`'s rows are implicit `AutoSize`
today (no explicit `RowStyles` set), so hiding both controls in a row should collapse its
rendered height; verify visually — if a gap remains, fall back to explicitly setting
`layout.RowStyles[rowIndex] = new RowStyle(SizeType.Absolute, isResource ? 0 : height)` for
the three affected rows.

`SaveSettings()` needs no change — it already unconditionally writes the (now
possibly-hidden) textbox values back to `_settings`; those fields just sit unused for
Resource (nothing reads `TunnelName`/`PacDirectory`/`PacPort` in Resource's `BuildChecks`),
and `Validate()` still passes since the underlying defaults are always valid.

**Proxy Address auto-fill**: mirror the existing `_tunnelNameModified`/`_settingTunnelName`
tracked-flag pattern (lines 8-9, 96-100, 128-137) with a new `_proxyAddressModified`/
`_settingProxyAddress` pair, so switching the combo to "Resource" live-fills the Proxy
Address textbox with `AppSettings.DefaultResourceProxyAddress` unless the user already
hand-edited it — same mechanism §2 applies at the CLI-launch layer, just for the live
dialog.

## 15. Docs — `readme.md` and `Setup/readme.md`

Both currently describe Resource as "install Python, pproxy, Windows Terminal profile,
startup shortcut," and describe the PAC server as a Python `http.server` process for
Hub/Travel — rewrite both:

- **`readme.md`**: `## What This Does` — the "Both modes start and own the Python PAC
  server (`python -m http.server`) on launch" line becomes "a native C# HTTP server" (no
  process, nothing to kill on exit beyond stopping the listener). `## Install` →
  `### Setup Order` Resource step description (line ~45) drops the Python/pproxy mention.
  `### What Each Mode Monitors` table (lines 79-85) needs a **Resource** column (`Resource
  VPN` via `localhost:8888`, `VPN Proxy` listener check) alongside Hub/Travel. `###
  Auto-Repair` note that Resource auto-repairs its own listener but VPN reconnect stays
  manual, same as Hub/Travel. `### Command Line Options` gains Resource's supported flags.
- **`Setup/readme.md`**: Hub Steps table (lines 14-30) drops the "Install Python" row (was
  step 8); Travel Steps table (lines 38-52) drops its "Install Python" row (was step 6);
  `### Resource Steps` table (lines 54-63) rewritten to 3 rows (Firewall rule, Startup
  Shortcut, Developer Settings). Delete the `### pproxy Setup` and `### Windows Terminal
  Profile (Resource)` sections (~228-261) entirely, replacing with a short note that the
  app itself is now the proxy — no Python, no pip, no Terminal profile anywhere. `### PAC
  File Setup` (~265-293) drops its "Install Python... winget install Python.Python.3.12"
  snippet — the PAC server is native now, nothing to install. `### Startup Shortcuts`
  Resource bullet (~349-360) updated to show it launches `ConduentResourceMonitor.exe
  --mode Resource`.

**CODE REVIEW FIX (Minor #7):** a repo-wide grep for `pproxy` after this plan was first
drafted turned up UI strings the doc-only pass above would have missed (these are in-app
text shown to the operator, not markdown — verified via grep, not speculative):
- `Setup/SetupModePicker.cs:39` — `"Resource — Conduent laptop; runs pproxy to share
  corporate VPN with Hub and Travel."` → reword to describe the native proxy (e.g. "runs
  the native proxy to share corporate VPN with Hub and Travel").
- `Setup/SetupPreflightForm.cs:203` — `"Resource setup will install Python, pproxy,
  firewall rule, Windows Terminal profile, and startup shortcut."` → update to match the
  new 3-step `BuildResource` list (§12) and mention the new Hub Static IP prompt (§12b).
- `Options.cs:18` and `Program.cs:250,272` — `"VPN/pproxy health check"` / `"VPN/pproxy
  HTTP to internal Conduent URL..."` — reword to drop "pproxy" (e.g. "VPN Proxy") for
  consistency now that pproxy doesn't exist anywhere in the app; low priority since these
  describe Hub/Travel's check (still accurate in spirit), but inconsistent once Resource's
  own help text (§11) says "VPN Proxy."
- `Setup/Steps/Hub/PortProxyRulesStep.cs:9` — code comment only (`(pproxy / VPN)`), lowest
  priority, optional cleanup.

---

## Verification

1. Run `dotnet run -- --mode Resource --show-log` on a dev box. Confirm the tray icon
   appears, the log shows `Conduent VPN` and `VPN Proxy` check lines each cycle, and
   Task Manager shows **zero** `python` child processes.
2. Run `dotnet run -- --mode Hub --show-log` (or Travel) and confirm `PAC Web Server`
   still passes and `Test-NetConnection localhost -Port <PacPort>` / a browser request to
   `http://localhost:<PacPort>/conduent-resource.pac` returns the file with
   `Content-Type: application/x-ns-proxy-autoconfig` — proves the native PAC server
   rewrite is a drop-in replacement. Confirm Task Manager shows zero `python` processes
   here too.
3. `Test-NetConnection localhost -Port 8888` succeeds while Resource mode is running,
   fails immediately after choosing Exit from the tray menu.
4. `curl -x http://localhost:8888 https://example.com` (or point a browser's proxy config
   at `localhost:8888`) — confirms the CONNECT + bidirectional TLS-passthrough relay path.
5. `curl -x http://localhost:8888 http://neverssl.com` — confirms the plain-HTTP
   absolute-URI forwarding path (the minority code path) also works.
6. With the corporate VPN actually connected, confirm the `Conduent VPN` check passes
   (real HTTP GET to `settings.CheckUrl` through `localhost:8888`). Disconnect VPN, confirm
   the check fails, one Windows toast fires, and the tray menu's `Fix: Check Conduent VPN`
   item shows the new local-wording MessageBox (not the Hub/Travel "remote into..." text).
7. Bind another process to port 8888 before launching Resource mode, confirm `Start()`'s
   catch swallows the bind failure, `VPN Proxy` check fails, auto-repair fires (capped
   at 2 attempts), and once the port frees up a repair succeeds and the check goes green.
8. Manually trigger `Fix: VPN Proxy` from the tray menu 2-3 times in a row, confirm no
   `SocketException: address already in use` on the following `Start()` — proves `Stop()`
   fully tears down the old listener before `Restart()`'s `Start()` rebinds. Do the same
   for the PAC server's restart path (change PAC Port/Directory in Settings on Hub/Travel
   a couple times) to prove the rewritten `PacServerService.Restart()` is equally clean.
9. Run `--setup Resource` end-to-end; confirm it no longer touches Python/pip/Windows
   Terminal settings.json, the firewall step still runs/verifies, and the created `.lnk`'s
   `TargetPath`/`Arguments` point at `ConduentResourceMonitor.exe --mode Resource`. Run
   `--setup Hub` and `--setup Travel` too and confirm neither attempts a Python install.
10. Open Settings, switch Mode to "Resource" — confirm Tunnel Name / PAC Directory / PAC
    Port rows disappear and Proxy Address auto-fills to `localhost:8888`; switch back to
    Hub/Travel, confirm the rows reappear. Also confirm a Resource-mode launch with a
    deliberately-corrupted `PacDirectory` in the shared settings file (simulating a prior
    Hub/Travel run on the same box) still starts cleanly — proves §2's mode-aware
    `Validate()` fix.
11. **CONNECT header draining (fix for review finding #1):** with the native proxy running,
    load several real HTTPS sites through it via a real browser (not just `curl`, since
    curl's CONNECT request shape can differ) — confirm pages load repeatedly and
    consistently, not just on the first request. This is the scenario the original bug
    would intermittently or consistently break.
12. **Plain-HTTP origin-form rewrite (fix for review finding #5):** `curl -x
    http://localhost:8888 http://neverssl.com` should return the page body, not a
    malformed-request error from the origin server.
13. **Firewall scoping (fix for review finding #2):** from a third machine on the home LAN
    that is *not* Hub, attempt `curl -x http://<Resource-LAN-IP>:8888 https://example.com`
    — should fail to connect (blocked by the `remoteip` restriction). From Hub itself,
    confirm the existing `Hub:8888` → `Resource:8888` port-proxy path still works
    end-to-end (the `remoteip` restriction must allow Hub's actual LAN IP, not just
    `localhost`).
14. **Immediate start-failure logging (fix for review finding #4):** bind another process to
    port 8888 (or the PAC port) before launching, then launch — confirm the log window
    shows a specific `LastError` message (e.g. "Address already in use") immediately at
    startup, not just a generic "Unreachable" from the check a cycle later.

### Critical files
- `Services/ProxyServerService.cs` (new — the proxy relay engine)
- `Services/PacServerService.cs` (rewritten — native PAC file server)
- `Services/RawHttpUtil.cs` (new — shared raw-socket line reader/writer)
- `TrayApp.cs` (mode-aware Start/Stop wiring, `BuildChecks`/`BuildRepairs` switches, immediate `LastError` logging)
- `AppSettings.cs` (Resource-aware `ProxyAddress` default, mode-aware `Validate()`)
- `Options.cs` (`AppMode` gains `Resource`)
- `Setup/StepFactory.cs` (`BuildResource`/`BuildHub`/`BuildTravel` step list changes)
- `Setup/SetupContext.cs` / `Setup/SetupPreflightForm.cs` (new `HubStaticIp` collection for firewall scoping)
- `Setup/Steps/Resource/PproxyFirewallStep.cs` (`profile=private` + `remoteip=<Hub IP>` scoping)
- `SettingsForm.cs` (mode combo + conditional row visibility)
- `Setup/Steps/Shared/StartupShortcutStep.cs` (3-way shortcut-target switch)
