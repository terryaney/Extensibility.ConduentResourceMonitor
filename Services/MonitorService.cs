using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Services;

public class MonitorService
{
	private static readonly TimeSpan NotifyCooldown = TimeSpan.FromMinutes( 15 );

	private readonly IReadOnlyList<ICheck> _checks;
	private readonly System.Windows.Forms.Timer _timer;
	private readonly Dictionary<string, DateTime> _lastNotifiedAt = [];
	private bool _running;

	public event Action<IReadOnlyList<CheckResult>>? ResultsUpdated;
	public event Action<IReadOnlyList<CheckResult>>? ChecksFailed;

	public IReadOnlyList<CheckResult> LastResults { get; private set; } = Array.Empty<CheckResult>();

	public MonitorService( IReadOnlyList<ICheck> checks, int intervalSeconds )
	{
		_checks = checks;
		_timer = new System.Windows.Forms.Timer { Interval = intervalSeconds * 1000 };
		_timer.Tick += OnTick;
	}

	private async void OnTick( object? sender, EventArgs e )
	{
		if ( _running ) return;

		_running = true;
		
		try { await RunChecksAsync(); }
		finally { _running = false; }
	}

	public async Task RunChecksAsync()
	{
		var results = new List<CheckResult>();
		foreach ( var check in _checks )
			results.Add( await check.RunAsync() );

		LastResults = results;
		ResultsUpdated?.Invoke( results );

		var now = DateTime.UtcNow;
		var candidates = new List<CheckResult>();
		foreach ( var r in results )
		{
			if ( r.Ok ) { _lastNotifiedAt.Remove( r.Name ); continue; }
			if ( !_lastNotifiedAt.TryGetValue( r.Name, out var last ) || now - last >= NotifyCooldown )
				candidates.Add( r );
		}

		if ( candidates.Count == 0 ) return;

		if ( !await InternetConnectivityCheck.IsOnlineAsync( TimeSpan.FromSeconds( 2 ) ) )
			return; // offline: stay silent, candidates remain eligible & re-probed next tick

		ChecksFailed?.Invoke( candidates );
		foreach ( var c in candidates )
			_lastNotifiedAt[ c.Name ] = now;
	}

	public void UpdateInterval( int seconds ) => _timer.Interval = seconds * 1000;

	public async Task Start()
	{
		await RunChecksAsync();
		_timer.Start();
	}

	public void Stop() => _timer.Stop();
}