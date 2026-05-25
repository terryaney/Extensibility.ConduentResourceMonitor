using System.Diagnostics;
using ConduentResourceMonitor.Checks;

namespace ConduentResourceMonitor.Repairs;

public class PortProxyRepair( AppSettings settings, ICheck check ) : IRepair
{
	private readonly AppSettings _settings = settings;

	public string Label => "Repair Port Proxy Rules";
	public string TargetCheckName => check.Name;
	public bool RequiresElevation => true;

	public void Execute() => Execute( startupDelay: false );

	public void Execute( bool startupDelay )
	{
		var connectHost = _settings.ProxyAddress.Contains( ':' )
			? _settings.ProxyAddress[ .._settings.ProxyAddress.LastIndexOf( ':' ) ]
			: _settings.ProxyAddress;

		var tempBat = Path.Combine( Path.GetTempPath(), "portproxy_repair.bat" );
		File.WriteAllText( tempBat, BuildScript( connectHost, startupDelay ) );

		Process.Start( new ProcessStartInfo( "cmd.exe", $"/c \"{tempBat}\"" )
		{
			Verb = "runas",
			UseShellExecute = true
		} );
	}

	private static string BuildScript( string connectHost, bool startupDelay ) => $"""
        @echo off
        {( startupDelay ? "echo [%time%] Waiting for network stack to settle...\r\ntimeout /t 60 /nobreak\r\n" : "" )}
        echo [%time%] Stopping IP Helper service...
        sc stop iphlpsvc

        timeout /t 10 /nobreak

        echo [%time%] Starting IP Helper service...
        sc start iphlpsvc

        timeout /t 15 /nobreak

        echo [%time%] Resetting portproxy rules...
        netsh interface portproxy reset

        echo [%time%] Adding portproxy rules...
        netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=8888 connectaddress={connectHost} connectport=8888
        netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=13389 connectaddress={connectHost} connectport=3389

        echo [%time%] Verifying...
        netstat -an | findstr "8888\|13389"

        echo [%time%] Done.
        del "%~f0"
        pause
        """;
}