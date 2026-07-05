using System.Text.RegularExpressions;

namespace ConduentResourceMonitor.Services.Sync;

public record ReconcileResult( SyncLedger Ledger, int Copies, int Trashed, int Conflicts, int Orphans, int Errors, string? FirstError, IReadOnlyList<string> PendingHydration );

// Pure reconcile — no timers or watchers, so it is testable with two temp directories.
// Deletes fire ONLY from ledger transitions, never from raw events or absence alone.
public class SyncEngine( string hubRoot, string resourceRoot, string trashDir, string conflictDir,
	IReadOnlyList<string> ignorePatterns, SyncLog log )
{
	private const string TempSuffix = ".crmsync-tmp";
	private const string HubSide = "Hub";
	private const string ResourceSide = "Resource";
	private static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds( 2 );  // OneDrive rounds/rewrites mtimes
	private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds( 500 ), TimeSpan.FromSeconds( 2 ), TimeSpan.FromSeconds( 5 )];

	private readonly Regex[] _ignoreRegexes = [.. ignorePatterns
		.Where( p => !string.IsNullOrWhiteSpace( p ) )
		.Select( p => new Regex( "^" + Regex.Escape( p.Trim() ).Replace( @"\*", ".*" ).Replace( @"\?", "." ) + "$",
			RegexOptions.IgnoreCase | RegexOptions.Compiled ) )];

	private sealed record FileState( long Size, DateTime LastWriteUtc );

	private int _copies, _trashed, _conflicts, _orphans, _errors;
	private string? _firstError;

	// Top-level dirs already confirmed fully hydrated on both sides — skip rechecking every pass.
	// Reset only by constructing a new engine (i.e. a FolderSyncService restart).
	private readonly HashSet<string> _confirmedReady = new( StringComparer.OrdinalIgnoreCase );

	public ReconcileResult Reconcile( SyncLedger? ledger, IReadOnlySet<string>? onlyTopDirs, CancellationToken ct )
	{
		_copies = _trashed = _conflicts = _orphans = _errors = 0;
		_firstError = null;

		// Guard: a missing root is an error, never a mass delete — OneDrive may not have the
		// folder mounted (sign-out, account issue) and absence must not read as deletion.
		if ( !Directory.Exists( hubRoot ) || !Directory.Exists( resourceRoot ) )
		{
			var missing = !Directory.Exists( hubRoot ) ? hubRoot : resourceRoot;
			RecordError( $"Sync root missing: {missing}" );
			return Result( ledger ?? new SyncLedger(), [] );
		}

		// First run (null ledger) needs no special casing: every file lands in the entry == null
		// branch of ProcessFile, which is an additive union merge with no deletes.
		ledger ??= new SyncLedger();

		var hubTopDirs = Directory.GetDirectories( hubRoot )
			.Select( d => Path.GetFileName( d )! )
			.ToHashSet( StringComparer.OrdinalIgnoreCase );

		// Orphan rule: a top-level dir removed from the HUB root leaves the Resource copy in
		// place and just stops syncing. Also applied to scoped dirs on targeted passes so a
		// hub-side top-dir delete that arrives as child events can never mass-trash Resource.
		foreach ( var root in ledger.SyncedRoots.ToList() )
		{
			ct.ThrowIfCancellationRequested();
			if ( onlyTopDirs != null && !onlyTopDirs.Contains( root ) ) continue;
			if ( hubTopDirs.Contains( root ) ) continue;

			ledger.SyncedRoots.Remove( root );
			var dropped = ledger.Entries.Keys
				.Where( k => TopSegment( k ).Equals( root, StringComparison.OrdinalIgnoreCase ) )
				.ToList();
			foreach ( var key in dropped )
				ledger.Entries.Remove( key );
			_orphans++;
			log.Line( $"ORPHAN '{root}' removed from Hub root — Resource copy left in place, {dropped.Count} ledger entries dropped, no longer syncing." );
		}

		var scopeDirs = onlyTopDirs == null
			? hubTopDirs
			: hubTopDirs.Where( onlyTopDirs.Contains ).ToHashSet( StringComparer.OrdinalIgnoreCase );

		// Auto-pin: cheap metadata flip, so it always runs immediately on the unpinned→managed
		// transition — no reason to gate it behind hydration like the content pass below.
		foreach ( var dir in scopeDirs )
		{
			ct.ThrowIfCancellationRequested();

			var hubDir = Path.Combine( hubRoot, dir );
			if ( !PinHelper.IsPinned( hubDir ) )
			{
				PinHelper.PinTree( hubDir );
				log.Line( $"PIN '{dir}' (Hub) — newly managed folder pinned." );
			}

			var resDir = Path.Combine( resourceRoot, dir );
			if ( Directory.Exists( resDir ) && !PinHelper.IsPinned( resDir ) )
			{
				PinHelper.PinTree( resDir );
				log.Line( $"PIN '{dir}' (Resource) — newly managed folder pinned." );
			}
		}

		// Hydration gate: only re-check dirs not already confirmed ready, and reuse the Scan below's
		// per-file walk to test it — no separate directory walk just for this.
		var checkHydrationDirs = scopeDirs.Where( d => !_confirmedReady.Contains( d ) ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		var unhydratedDirs = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		var hubFiles = Scan( hubRoot, scopeDirs, checkHydrationDirs, unhydratedDirs );
		var resFiles = Scan( resourceRoot, scopeDirs, checkHydrationDirs, unhydratedDirs );

		foreach ( var dir in checkHydrationDirs )
			if ( !unhydratedDirs.Contains( dir ) )
				_confirmedReady.Add( dir );

		foreach ( var dir in unhydratedDirs )
			log.Line( $"HYDRATING '{dir}' — still downloading from OneDrive; content sync skipped this pass." );

		var pendingHydration = unhydratedDirs.OrderBy( d => d, StringComparer.OrdinalIgnoreCase ).ToList();

		var keys = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		keys.UnionWith( hubFiles.Keys );
		keys.UnionWith( resFiles.Keys );
		foreach ( var key in ledger.Entries.Keys )
			if ( ( onlyTopDirs == null || onlyTopDirs.Contains( TopSegment( key ) ) ) && !unhydratedDirs.Contains( TopSegment( key ) ) )
				keys.Add( key );

		foreach ( var relPath in keys.OrderBy( k => k, StringComparer.OrdinalIgnoreCase ) )
		{
			ct.ThrowIfCancellationRequested();
			hubFiles.TryGetValue( relPath, out var hub );
			resFiles.TryGetValue( relPath, out var res );
			ledger.Entries.TryGetValue( relPath, out var entry );
			try
			{
				ProcessFile( ledger, relPath, hub, res, entry );
			}
			catch ( Exception ex ) when ( ex is not OperationCanceledException )
			{
				RecordError( $"'{relPath}': {ex.Message}" );
			}
		}

		foreach ( var dir in scopeDirs )
			if ( !ledger.SyncedRoots.Contains( dir, StringComparer.OrdinalIgnoreCase ) )
				ledger.SyncedRoots.Add( dir );

		return Result( ledger, pendingHydration );
	}

	// 3-way compare of one relPath: current hub state, current resource state, last-known ledger state.
	private void ProcessFile( SyncLedger ledger, string relPath, FileState? hub, FileState? res, LedgerEntry? entry )
	{
		if ( entry == null )
		{
			// New to the ledger (first run, or file created since last pass)
			if ( hub != null && res == null )
				CopyAcross( ledger, relPath, hubToRes: true );
			else if ( hub == null && res != null )
				CopyAcross( ledger, relPath, hubToRes: false );
			else if ( hub != null && res != null )
			{
				if ( Same( hub, res ) )
					RecordEntry( ledger, relPath );
				else
					ResolveConflict( ledger, relPath, hub, res );
			}
			return;
		}

		var hubChanged = Changed( hub, entry.Hub );
		var resChanged = Changed( res, entry.Resource );

		if ( hub == null && res == null )
		{
			ledger.Entries.Remove( relPath );  // deleted on both sides independently — nothing to do
		}
		else if ( hub != null && res == null )
		{
			// Resource side deleted. Delete-vs-edit race: an edited hub copy wins and is resurrected.
			if ( hubChanged )
			{
				log.Line( $"RESURRECT '{relPath}' — deleted on Resource but edited on Hub; edit wins." );
				CopyAcross( ledger, relPath, hubToRes: true );
			}
			else
				SoftDelete( ledger, relPath, hubSide: true );
		}
		else if ( hub == null && res != null )
		{
			if ( resChanged )
			{
				log.Line( $"RESURRECT '{relPath}' — deleted on Hub but edited on Resource; edit wins." );
				CopyAcross( ledger, relPath, hubToRes: false );
			}
			else
				SoftDelete( ledger, relPath, hubSide: false );
		}
		else
		{
			if ( hubChanged && !resChanged )
				CopyAcross( ledger, relPath, hubToRes: true );
			else if ( !hubChanged && resChanged )
				CopyAcross( ledger, relPath, hubToRes: false );
			else if ( hubChanged && resChanged )
			{
				if ( Same( hub!, res! ) )
					RecordEntry( ledger, relPath );  // OneDrive echo of our own copy — both drifted to the same state
				else
					ResolveConflict( ledger, relPath, hub!, res! );
			}
			// neither changed → no-op
		}
	}

	private void CopyAcross( SyncLedger ledger, string relPath, bool hubToRes )
	{
		var src = Path.Combine( hubToRes ? hubRoot : resourceRoot, relPath );
		var destRoot = hubToRes ? resourceRoot : hubRoot;
		var dest = Path.Combine( destRoot, relPath );
		var destSide = hubToRes ? ResourceSide : HubSide;

		var ok = WithRetry( $"copy '{relPath}' {( hubToRes ? "Hub→Resource" : "Resource→Hub" )}", () =>
		{
			var destDir = Path.GetDirectoryName( dest )!;
			// Only log when this directory is genuinely new — every copy pins its dest file too,
			// so logging that on every call would drown the COPY line in per-file noise.
			if ( !Directory.Exists( destDir ) )
			{
				Directory.CreateDirectory( destDir );
				PinHelper.Pin( destDir );
				log.Line( $"PIN '{Path.GetRelativePath( destRoot, destDir )}' ({destSide}) — new folder pinned." );
			}
			// Copy to a temp name then rename: a direct copy exposes a half-written file to the
			// OneDrive client watching the destination; a same-volume rename is atomic on NTFS,
			// so the real filename only ever shows complete content. Scans skip .crmsync-tmp.
			var tmp = dest + TempSuffix;
			File.Copy( src, tmp, overwrite: true );
			File.SetLastWriteTimeUtc( tmp, File.GetLastWriteTimeUtc( src ) );
			File.Move( tmp, dest, overwrite: true );
			PinHelper.Pin( dest );
		} );

		if ( !ok ) return;  // ledger entry left unchanged so the next pass retries

		_copies++;
		log.Line( $"COPY '{relPath}' {( hubToRes ? "Hub→Resource" : "Resource→Hub" )}" );
		RecordEntry( ledger, relPath );
	}

	private void SoftDelete( SyncLedger ledger, string relPath, bool hubSide )
	{
		var src = Path.Combine( hubSide ? hubRoot : resourceRoot, relPath );
		var dest = Path.Combine( trashDir, relPath + "." + Stamp() );

		var ok = WithRetry( $"trash '{relPath}' ({( hubSide ? HubSide : ResourceSide )})", () =>
		{
			Directory.CreateDirectory( Path.GetDirectoryName( dest )! );
			File.Move( src, dest, overwrite: true );
		} );

		if ( !ok ) return;

		_trashed++;
		ledger.Entries.Remove( relPath );
		log.Line( $"TRASH '{relPath}' — deleted on {( hubSide ? ResourceSide : HubSide )}, {( hubSide ? HubSide : ResourceSide )} copy moved to SyncTrash." );
	}

	private void ResolveConflict( SyncLedger ledger, string relPath, FileState hub, FileState res )
	{
		var hubWins = hub.LastWriteUtc >= res.LastWriteUtc;
		var loserSide = hubWins ? ResourceSide : HubSide;
		var loserAbs = Path.Combine( hubWins ? resourceRoot : hubRoot, relPath );
		var relDir = Path.GetDirectoryName( relPath ) ?? "";
		var conflictDest = Path.Combine( conflictDir, relDir, $"{Path.GetFileName( relPath )}.{loserSide}.{Stamp()}" );

		var ok = WithRetry( $"preserve conflict loser '{relPath}' ({loserSide})", () =>
		{
			Directory.CreateDirectory( Path.GetDirectoryName( conflictDest )! );
			File.Move( loserAbs, conflictDest, overwrite: true );
		} );

		if ( !ok ) return;

		_conflicts++;
		log.Line( $"CONFLICT '{relPath}' — both sides changed; {( hubWins ? HubSide : ResourceSide )} is newer and wins, {loserSide} copy preserved in SyncConflict." );
		CopyAcross( ledger, relPath, hubToRes: hubWins );
	}

	// Records what is actually on disk right now for both sides — per-side because OneDrive
	// may later rewrite one side's mtime and we must detect that drift independently.
	private void RecordEntry( SyncLedger ledger, string relPath )
	{
		var entry = new LedgerEntry();
		var hubInfo = new FileInfo( Path.Combine( hubRoot, relPath ) );
		var resInfo = new FileInfo( Path.Combine( resourceRoot, relPath ) );
		if ( hubInfo.Exists ) entry.Hub = new LedgerFileState { Size = hubInfo.Length, LastWriteUtc = hubInfo.LastWriteTimeUtc };
		if ( resInfo.Exists ) entry.Resource = new LedgerFileState { Size = resInfo.Length, LastWriteUtc = resInfo.LastWriteTimeUtc };
		ledger.Entries[ relPath ] = entry;
	}

	private static bool Changed( FileState? current, LedgerFileState? known )
	{
		if ( ( current == null ) != ( known == null ) ) return true;
		if ( current == null || known == null ) return false;
		return current.Size != known.Size ||
			( current.LastWriteUtc - known.LastWriteUtc ).Duration() > MtimeTolerance;
	}

	private static bool Same( FileState a, FileState b ) =>
		a.Size == b.Size && ( a.LastWriteUtc - b.LastWriteUtc ).Duration() <= MtimeTolerance;

	// checkHydrationDirs/unhydratedDirs let hydration-checking reuse this same per-file walk instead
	// of a second directory enumeration: a dir being checked is scanned into a side buffer first: if
	// every file turns out hydrated its entries merge into the result as normal, otherwise the whole
	// dir's entries are dropped (excluded from compare/copy this pass) and it's flagged unhydrated.
	private Dictionary<string, FileState> Scan( string root, IReadOnlySet<string> topDirs,
		HashSet<string> checkHydrationDirs, HashSet<string> unhydratedDirs )
	{
		var result = new Dictionary<string, FileState>( StringComparer.OrdinalIgnoreCase );
		foreach ( var top in topDirs )
		{
			var dir = Path.Combine( root, top );
			if ( !Directory.Exists( dir ) ) continue;

			var checkHydration = checkHydrationDirs.Contains( top );
			var dirHydrated = true;
			var pending = checkHydration ? new Dictionary<string, FileState>( StringComparer.OrdinalIgnoreCase ) : null;

			try
			{
				// EnumerateFiles is lazy — an inaccessible subfolder anywhere in this tree throws
				// mid-walk. Scoped per top dir so one bad subfolder only drops that dir this pass
				// instead of aborting Scan (and the whole Reconcile) for every other folder too.
				foreach ( var file in Directory.EnumerateFiles( dir, "*", SearchOption.AllDirectories ) )
				{
					var name = Path.GetFileName( file );
					if ( name.EndsWith( TempSuffix, StringComparison.OrdinalIgnoreCase ) ) continue;
					if ( _ignoreRegexes.Any( rx => rx.IsMatch( name ) ) ) continue;
					var info = new FileInfo( file );
					var relPath = Path.GetRelativePath( root, file );
					var state = new FileState( info.Length, info.LastWriteTimeUtc );

					if ( checkHydration )
					{
						if ( dirHydrated && !PinHelper.IsHydrated( file ) ) dirHydrated = false;
						pending![ relPath ] = state;
					}
					else
						result[ relPath ] = state;
				}
			}
			catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
			{
				RecordError( $"Scan '{top}' ({( root == hubRoot ? HubSide : ResourceSide )}) failed: {ex.Message}" );
				continue;
			}

			if ( checkHydration )
			{
				if ( dirHydrated )
					foreach ( var kv in pending! )
						result[ kv.Key ] = kv.Value;
				else
					unhydratedDirs.Add( top );
			}
		}
		return result;
	}

	// Locked files (open Office docs, AV scans) get 3 spaced retries; final failure leaves the
	// ledger entry untouched so the next pass retries the same operation.
	private bool WithRetry( string description, Action op )
	{
		for ( var attempt = 0; ; attempt++ )
		{
			try
			{
				op();
				return true;
			}
			catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
			{
				if ( attempt >= RetryDelays.Length )
				{
					RecordError( $"{description} failed after {RetryDelays.Length} retries: {ex.Message}" );
					return false;
				}
				log.Line( $"RETRY {attempt + 1}/{RetryDelays.Length} {description}: {ex.Message}" );
				Thread.Sleep( RetryDelays[ attempt ] );
			}
		}
	}

	private void RecordError( string message )
	{
		_errors++;
		_firstError ??= message;
		log.Line( $"ERROR {message}" );
	}

	private static string TopSegment( string relPath )
	{
		var idx = relPath.IndexOfAny( ['\\', '/'] );
		return idx < 0 ? relPath : relPath[ ..idx ];
	}

	private static string Stamp() => DateTime.Now.ToString( "yyyyMMdd-HHmmss" );

	private ReconcileResult Result( SyncLedger ledger, IReadOnlyList<string> pendingHydration ) =>
		new( ledger, _copies, _trashed, _conflicts, _orphans, _errors, _firstError, pendingHydration );
}
