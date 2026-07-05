using System.Runtime.InteropServices;

namespace ConduentResourceMonitor.Services.Sync;

// The app pins matching top-level sync folders itself — FILE_ATTRIBUTE_PINNED is a metadata-only
// flip, so this is instant. FolderSyncService/SyncEngine separately gate actual content reads on
// hydration (RECALL_ON_DATA_ACCESS clear) so a pin never triggers a synchronous OneDrive download.
public static class PinHelper
{
	private const int FILE_ATTRIBUTE_PINNED = 0x00080000;
	private const int FILE_ATTRIBUTE_UNPINNED = 0x00100000;
	private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

	[DllImport( "kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode )]
	private static extern bool SetFileAttributesW( string lpFileName, int dwFileAttributes );

	public static bool IsPinned( string path )
	{
		try
		{
			return ( (int)File.GetAttributes( path ) & FILE_ATTRIBUTE_PINNED ) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsHydrated( string path )
	{
		try
		{
			return ( (int)File.GetAttributes( path ) & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS ) == 0;
		}
		catch
		{
			return true; // nothing to wait for if the path is gone
		}
	}

	public static void Pin( string path )
	{
		try
		{
			var attrs = (int)File.GetAttributes( path );
			attrs |= FILE_ATTRIBUTE_PINNED;
			attrs &= ~FILE_ATTRIBUTE_UNPINNED;
			SetFileAttributesW( path, attrs );
		}
		catch
		{
			// Best-effort — retried on the next pass that sees this folder unpinned
		}
	}

	public static void PinTree( string root )
	{
		Pin( root );
		try
		{
			foreach ( var dir in Directory.EnumerateDirectories( root, "*", SearchOption.AllDirectories ) )
				Pin( dir );
			foreach ( var file in Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories ) )
				Pin( file );
		}
		catch
		{
			// Best-effort — retried on the next pass that sees this folder unpinned
		}
	}
}
