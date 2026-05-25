using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Services;

public class MonitorService
{
	private readonly IReadOnlyList<ICheck> _checks;
	private readonly System.Windows.Forms.Timer _timer;
	private readonly Dictionary<string, bool?> _lastState = [];
	private bool _running;

	public event Action<IReadOnlyList<CheckResult>>? ResultsUpdated;
	public event Action<CheckResult>? CheckFailed;

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

		foreach ( var r in results )
		{
			var wasOk = _lastState.GetValueOrDefault( r.Name );
			if ( !r.Ok && wasOk != false )
				CheckFailed?.Invoke( r );
			_lastState[ r.Name ] = r.Ok;
		}
	}

	public void UpdateInterval( int seconds ) => _timer.Interval = seconds * 1000;

	public async Task Start()
	{
		await RunChecksAsync();
		_timer.Start();
	}

	public void Stop() => _timer.Stop();
}