using System.Net.Sockets;
using System.Text;

namespace ConduentResourceMonitor.Services;

internal static class RawHttpUtil
{
	public static async Task<string?> ReadLineAsync( NetworkStream stream, CancellationToken token )
	{
		var buffer = new List<byte>();
		var b = new byte[ 1 ];
		while ( true )
		{
			var read = await stream.ReadAsync( b, token );
			if ( read == 0 ) return buffer.Count == 0 ? null : Encoding.ASCII.GetString( [ .. buffer ] );
			if ( b[ 0 ] == '\n' ) break;
			if ( b[ 0 ] != '\r' ) buffer.Add( b[ 0 ] );
		}
		return Encoding.ASCII.GetString( [ .. buffer ] );
	}

	public static Task WriteAsciiAsync( NetworkStream stream, string text, CancellationToken token = default ) =>
		stream.WriteAsync( Encoding.ASCII.GetBytes( text ), token ).AsTask();
}
