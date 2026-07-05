namespace ConduentResourceMonitor.Services.Sync;

// Append-only sync activity log with a simple 1MB rollover to sync.log.1. Lines are optionally
// mirrored to the LogForm so tray users see sync activity without opening the file.
public class SyncLog( string path, long maxBytes = 1_000_000, Action<string>? mirror = null )
{
	private readonly object _lock = new();

	public void Line( string msg )
	{
		var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
		lock ( _lock )
		{
			try
			{
				var info = new FileInfo( path );
				if ( info.Exists && info.Length >= maxBytes )
					File.Move( path, path + ".1", overwrite: true );
				File.AppendAllText( path, stamped + Environment.NewLine );
			}
			catch
			{
				// Logging must never take down sync itself (disk full, AV lock, etc.)
			}
		}
		mirror?.Invoke( stamped );
	}
}
