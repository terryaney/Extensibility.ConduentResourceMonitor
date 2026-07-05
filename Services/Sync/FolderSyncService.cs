namespace ConduentResourceMonitor.Services.Sync;

public record SyncStatus( bool Reconciling, bool Paused, IReadOnlyList<string> PendingHydration, DateTime? LastSyncLocal, int ErrorCount, string? LastError, int TrashFileCount );

// Orchestrates the pure SyncEngine: two FileSystemWatchers feed coalesced dirty flags to a
// single worker task (exactly one reconcile at a time; full supersedes targeted), plus a
// 10-minute full-reconcile timer. Pinning and hydration-waiting are handled inside SyncEngine —
// watchers just stay enabled whenever the service isn't paused.
public class FolderSyncService : IDisposable
{
	private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds( 3 );
	private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds( 500 );
	private static readonly TimeSpan FullReconcileInterval = TimeSpan.FromMinutes( 10 );

	private readonly AppSettings _settings;
	private readonly SynchronizationContext? _uiContext;  // construct on UI thread — captured here
	private readonly SyncLog _log;
	private readonly SyncEngine _engine;
	private readonly string _ledgerPath;
	private readonly string _trashDir;
	private readonly List<FileSystemWatcher> _watchers = [];

	// Coalesced work flags — all mutated under _lock, drained by the worker
	private readonly object _lock = new();
	private bool _fullPending;
	private readonly HashSet<string> _dirtyTopDirs = new( StringComparer.OrdinalIgnoreCase );
	private DateTime _lastFsEventUtc;
	private readonly SemaphoreSlim _wake = new( 0 );

	private readonly CancellationTokenSource _cts = new();
	private Task? _worker;
	private System.Threading.Timer? _timer;

	private SyncLedger? _ledger;
	private bool _paused;
	private bool _watchersEnabled;
	private bool _reconciling;
	private DateTime? _lastSyncLocal;
	private int _errorCount;
	private string? _lastError;
	private IReadOnlyList<string> _pendingHydration = [];

	public event Action<SyncStatus>? StatusChanged;

	public FolderSyncService( AppSettings settings, Action<string>? uiLog )
	{
		_settings = settings;
		_uiContext = SynchronizationContext.Current;
		_log = new SyncLog( Path.Combine( AppContext.BaseDirectory, "sync.log" ), mirror: uiLog );
		_ledgerPath = Path.Combine( AppContext.BaseDirectory, "sync.ledger.json" );
		_trashDir = Path.Combine( AppContext.BaseDirectory, "SyncTrash" );
		var conflictDir = Path.Combine( AppContext.BaseDirectory, "SyncConflict" );
		var patterns = settings.SyncIgnorePatterns.Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		_engine = new SyncEngine( settings.HubSyncPath, settings.ResourceSyncPath, _trashDir, conflictDir, patterns, _log );
	}

	public void Start()
	{
		_ledger = SyncLedger.Load( _ledgerPath );
		_paused = _settings.SyncPaused;
		_worker = Task.Run( () => WorkerLoopAsync( _cts.Token ) );

		foreach ( var root in new[] { _settings.HubSyncPath, _settings.ResourceSyncPath } )
		{
			var watcher = new FileSystemWatcher( root )
			{
				IncludeSubdirectories = true,
				InternalBufferSize = 64 * 1024,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
			};
			var rootCopy = root;
			watcher.Created += ( _, e ) => OnFsEvent( rootCopy, e.FullPath );
			watcher.Changed += ( _, e ) => OnFsEvent( rootCopy, e.FullPath );
			watcher.Deleted += ( _, e ) => OnFsEvent( rootCopy, e.FullPath );
			watcher.Renamed += ( _, e ) => { OnFsEvent( rootCopy, e.OldFullPath ); OnFsEvent( rootCopy, e.FullPath ); };
			// Buffer overflow means missed events — a full reconcile recovers whatever was lost
			watcher.Error += ( _, _ ) => QueueFull();
			_watchers.Add( watcher );
		}

		if ( _paused )
		{
			_log.Line( "Sync paused (persisted from last session)." );
			PostStatus();
			return;
		}

		SetWatchersEnabled( true );
		QueueFull();
		_timer = new System.Threading.Timer( _ => OnTimerTick(), null, FullReconcileInterval, FullReconcileInterval );
	}

	public void Pause()
	{
		_paused = true;
		SetWatchersEnabled( false );
		_timer?.Dispose();
		_timer = null;
		lock ( _lock )
		{
			_fullPending = false;
			_dirtyTopDirs.Clear();
		}
		_log.Line( "Sync paused." );
		PostStatus();
	}

	public void Resume()
	{
		_paused = false;
		_log.Line( "Sync resumed." );
		SetWatchersEnabled( true );
		QueueFull();
		_timer?.Dispose();
		_timer = new System.Threading.Timer( _ => OnTimerTick(), null, FullReconcileInterval, FullReconcileInterval );
	}

	public void RequestFullReconcile()
	{
		if ( !_paused && _watchersEnabled ) QueueFull();
	}

	public int GetTrashFileCount()
	{
		try
		{
			return Directory.Exists( _trashDir )
				? Directory.EnumerateFiles( _trashDir, "*", SearchOption.AllDirectories ).Count()
				: 0;
		}
		catch
		{
			return 0;
		}
	}

	public void PurgeTrash()
	{
		try
		{
			if ( Directory.Exists( _trashDir ) )
				Directory.Delete( _trashDir, recursive: true );
			_log.Line( "SyncTrash purged." );
		}
		catch ( Exception ex )
		{
			_log.Line( $"ERROR purging SyncTrash: {ex.Message}" );
		}
		PostStatus();
	}

	public void Stop()
	{
		if ( !_cts.IsCancellationRequested ) _cts.Cancel();
		_timer?.Dispose();
		_timer = null;
		foreach ( var watcher in _watchers )
			watcher.Dispose();
		_watchers.Clear();
		try
		{
			_worker?.Wait( TimeSpan.FromSeconds( 10 ) );
		}
		catch
		{
			// Worker faulted or cancelled during shutdown — nothing actionable
		}
	}

	public void Dispose()
	{
		Stop();
		_cts.Dispose();
		_wake.Dispose();
		GC.SuppressFinalize( this );
	}

	private void OnFsEvent( string root, string fullPath )
	{
		string relative;
		try
		{
			relative = Path.GetRelativePath( root, fullPath );
		}
		catch
		{
			return;
		}
		if ( relative.EndsWith( ".crmsync-tmp", StringComparison.OrdinalIgnoreCase ) ) return;

		lock ( _lock )
		{
			var separator = relative.IndexOfAny( ['\\', '/'] );
			if ( separator < 0 )
				_fullPending = true;  // event at the root itself — top dir create/delete/rename
			else
				_dirtyTopDirs.Add( relative[ ..separator ] );
			_lastFsEventUtc = DateTime.UtcNow;
		}
		_wake.Release();
	}

	private void QueueFull()
	{
		lock ( _lock )
			_fullPending = true;
		_wake.Release();
	}

	private void OnTimerTick() => QueueFull();

	private void SetWatchersEnabled( bool enabled )
	{
		_watchersEnabled = enabled;
		foreach ( var watcher in _watchers )
			watcher.EnableRaisingEvents = enabled;
	}

	private async Task WorkerLoopAsync( CancellationToken ct )
	{
		try
		{
			while ( !ct.IsCancellationRequested )
			{
				await _wake.WaitAsync( ct );

				while ( !ct.IsCancellationRequested )
				{
					bool ready;
					var runFull = false;
					HashSet<string>? dirty = null;
					lock ( _lock )
					{
						if ( !_fullPending && _dirtyTopDirs.Count == 0 ) break;

						// Full runs immediately; targeted waits for a 3s quiet window so bursts
						// (saves, unzips) coalesce into one pass
						ready = _fullPending || DateTime.UtcNow - _lastFsEventUtc >= QuietWindow;
						if ( ready )
						{
							runFull = _fullPending;
							if ( !runFull ) dirty = new HashSet<string>( _dirtyTopDirs, StringComparer.OrdinalIgnoreCase );
							_fullPending = false;
							_dirtyTopDirs.Clear();
						}
					}

					if ( !ready )
					{
						await Task.Delay( PollDelay, ct );
						continue;
					}

					RunReconcile( runFull ? null : dirty, ct );
				}
			}
		}
		catch ( OperationCanceledException )
		{
			// Normal shutdown
		}
	}

	private void RunReconcile( IReadOnlySet<string>? onlyTopDirs, CancellationToken ct )
	{
		_reconciling = true;
		PostStatus();
		try
		{
			var result = _engine.Reconcile( _ledger, onlyTopDirs, ct );
			_ledger = result.Ledger;
			_ledger.Save( _ledgerPath );
			_lastSyncLocal = DateTime.Now;
			_errorCount = result.Errors;
			_lastError = result.FirstError;
			_pendingHydration = result.PendingHydration;
			if ( result.Copies + result.Trashed + result.Conflicts + result.Orphans + result.Errors > 0 )
				_log.Line( $"Reconcile ({( onlyTopDirs == null ? "full" : "targeted" )}): {result.Copies} copied, {result.Trashed} trashed, {result.Conflicts} conflicts, {result.Orphans} orphans, {result.Errors} errors." );
		}
		catch ( OperationCanceledException )
		{
			throw;
		}
		catch ( Exception ex )
		{
			_errorCount = 1;
			_lastError = ex.Message;
			_log.Line( $"ERROR reconcile failed: {ex.Message}" );
		}
		finally
		{
			_reconciling = false;
		}
		PostStatus();
	}

	private void PostStatus()
	{
		var status = new SyncStatus(
			Reconciling: _reconciling,
			Paused: _paused,
			PendingHydration: _pendingHydration,
			LastSyncLocal: _lastSyncLocal,
			ErrorCount: _errorCount,
			LastError: _lastError,
			TrashFileCount: GetTrashFileCount()
		);
		if ( _uiContext != null )
			_uiContext.Post( _ => StatusChanged?.Invoke( status ), null );
		else
			StatusChanged?.Invoke( status );
	}
}
