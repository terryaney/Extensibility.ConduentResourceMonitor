using System.Text.Json;

namespace ConduentResourceMonitor.Services.Sync;

// Per-side state is deliberate: OneDrive can rewrite mtimes during hydration, so "hub changed"
// and "resource changed" must be evaluated independently against what each side looked like
// after the last sync.
public class LedgerFileState
{
	public long Size { get; set; }
	public DateTime LastWriteUtc { get; set; }
}

public class LedgerEntry
{
	public LedgerFileState? Hub { get; set; }
	public LedgerFileState? Resource { get; set; }
}

public class SyncLedger
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public int Version { get; set; } = 1;
	public List<string> SyncedRoots { get; set; } = [];  // top-level dir names — powers orphan detection
	public Dictionary<string, LedgerEntry> Entries { get; set; } = new( StringComparer.OrdinalIgnoreCase );  // key = relPath

	// Null (not empty) when absent or unreadable → first-run additive merge, which never deletes.
	public static SyncLedger? Load( string path )
	{
		if ( !File.Exists( path ) ) return null;
		try
		{
			var ledger = JsonSerializer.Deserialize<SyncLedger>( File.ReadAllText( path ), JsonOptions );
			if ( ledger == null ) return null;
			// Deserialization produces a default-comparer dictionary — relPath keys must be case-insensitive
			ledger.Entries = new Dictionary<string, LedgerEntry>( ledger.Entries, StringComparer.OrdinalIgnoreCase );
			return ledger;
		}
		catch
		{
			return null;
		}
	}

	public void Save( string path )
	{
		// Write-then-rename so a crash mid-save never leaves a truncated ledger (which would
		// read as corrupt → first-run → no deletes, but still loses delete tracking)
		var tmp = path + ".tmp";
		File.WriteAllText( tmp, JsonSerializer.Serialize( this, JsonOptions ) );
		File.Move( tmp, path, overwrite: true );
	}
}
