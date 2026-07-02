using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConduentResourceMonitor.Setup;

internal sealed class ElevatedCommand
{
	public required string FileName { get; init; }
	public string[] Arguments { get; init; } = [];
	public int[] SuccessExitCodes { get; init; } = [0];
	public string? Description { get; init; }
}

internal static class ProcessHelper
{
	public const string WireGuardExePath = @"C:\Program Files\WireGuard\wireguard.exe";
	private static readonly Regex WireGuardKeyRegex = new( "^[A-Za-z0-9+/]{43}=$", RegexOptions.Compiled );

	private static readonly Dictionary<string, string> AllowedElevatedExecutables = new( StringComparer.OrdinalIgnoreCase )
	{
		["netsh"] = Path.Combine( Environment.SystemDirectory, "netsh.exe" ),
		["sc"] = Path.Combine( Environment.SystemDirectory, "sc.exe" ),
		["powershell.exe"] = Path.Combine( Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe" ),
		["wireguard.exe"] = WireGuardExePath
	};

	public static async Task<(int ExitCode, string Output)> RunAsync( string exe, string args )
	{
		var psi = new ProcessStartInfo( exe, args )
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		return await RunCoreAsync( psi );
	}

	public static async Task<(int ExitCode, string Output)> RunWithInputAsync( string exe, string args, string stdin )
	{
		var psi = new ProcessStartInfo( exe, args )
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		return await RunCoreAsync( psi, stdin );
	}

	public static async Task<(int ExitCode, string Output)> RunPowerShellAsync( string script )
	{
		var temp = Path.Combine( Path.GetTempPath(), $"setup_{Guid.NewGuid():N}.ps1" );
		try
		{
			File.WriteAllText( temp, script );
			return await RunAsync( "powershell", $"-ExecutionPolicy Bypass -File \"{temp}\"" );
		}
		finally
		{
			if ( File.Exists( temp ) ) File.Delete( temp );
		}
	}

	public static async Task<(int ExitCode, string Output)> RunElevatedCommandsWithOutputAsync( IReadOnlyList<ElevatedCommand> commands, Action<string>? logLine = null, bool continueOnFailure = false )
	{
		var normalizedCommands = NormalizeElevatedCommands( commands );
		var tempLog = Path.Combine( Path.GetTempPath(), $"setup_{Guid.NewGuid():N}.log" );
		var exitCode = -1;

		try
		{
			var payload = JsonSerializer.Serialize( normalizedCommands.Select( c => new
			{
				c.FileName,
				c.Arguments,
				c.SuccessExitCodes,
				c.Description
			} ) );
			var payloadB64 = Convert.ToBase64String( System.Text.Encoding.UTF8.GetBytes( payload ) );
			var escapedLogPath = tempLog.Replace( "'", "''" );
			var continueOnFailureLiteral = continueOnFailure ? "$true" : "$false";

			var ps =
				"$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + payloadB64 + "'));" +
				"$commands=ConvertFrom-Json -InputObject $json;" +
				"$sb=New-Object System.Text.StringBuilder;" +
				"$overall=0;" +
				"$continueOnFailure=" + continueOnFailureLiteral + ";" +
				"foreach($c in $commands){" +
				" if($c.Description){[void]$sb.AppendLine(('>> ' + [string]$c.Description));}" +
				" $exe=[string]$c.FileName;" +
				" $argsArray=@($c.Arguments | ForEach-Object {[string]$_});" +
				" $cmdExit=0;" +
				" try{$o=(& $exe @argsArray 2>&1 | Out-String); if($o){[void]$sb.AppendLine($o.TrimEnd())}; if($null -ne $LASTEXITCODE){$cmdExit=[int]$LASTEXITCODE}else{$cmdExit=0}}catch{[void]$sb.AppendLine($_.Exception.Message);$cmdExit=1}" +
				" $ok=@(0); if($null -ne $c.SuccessExitCodes -and @($c.SuccessExitCodes).Count -gt 0){$ok=@($c.SuccessExitCodes | ForEach-Object {[int]$_});}" +
				" if($ok -notcontains [int]$cmdExit){[void]$sb.AppendLine(('Exit code: ' + $cmdExit)); if([int]$cmdExit -eq 0){$overall=1}else{$overall=[int]$cmdExit}; if(-not $continueOnFailure){break}}" +
				"}" +
				"[System.IO.File]::WriteAllText('" + escapedLogPath + "',$sb.ToString());" +
				"exit $overall;";

			var encodedPs = Convert.ToBase64String( System.Text.Encoding.Unicode.GetBytes( ps ) );
			var psi = new ProcessStartInfo( AllowedElevatedExecutables[ "powershell.exe" ], $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedPs}" )
			{
				Verb = "runas",
				UseShellExecute = true
			};

			using var p = Process.Start( psi );
			if ( p == null ) return (-1, "Failed to start elevated command process.");

			await p.WaitForExitAsync();
			exitCode = p.ExitCode;

			var output = File.Exists( tempLog ) ? File.ReadAllText( tempLog ) : string.Empty;
			if ( output.Length > 0 && logLine != null )
			{
				foreach ( var line in output.Split( ["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries ) )
					logLine( line );
			}

			return (exitCode, output.Trim());
		}
		catch ( Exception ex )
		{
			return (exitCode, ex.Message);
		}
		finally
		{
			try
			{
				if ( File.Exists( tempLog ) ) File.Delete( tempLog );
			}
			catch
			{
				// Best-effort cleanup only; never mask the command result.
			}
		}
	}

	public static string BuildElevatedFailureMessage( string action, int exitCode, string output )
	{
		var detail = ExtractElevatedFailureDetail( output );
		if ( string.IsNullOrWhiteSpace( detail ) )
		{
			detail = exitCode switch
			{
				-1 => "The elevated command did not start or the UAC prompt was canceled.",
				_ => $"Exit code: {exitCode}"
			};
		}

		return $"{action} failed: {detail}";
	}

	private static string ExtractElevatedFailureDetail( string output )
	{
		if ( string.IsNullOrWhiteSpace( output ) )
			return string.Empty;

		var lines = output.Split( ["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		var exitIndex = Array.FindLastIndex( lines, line => line.StartsWith( "Exit code:", StringComparison.OrdinalIgnoreCase ) );
		if ( exitIndex >= 0 )
		{
			var sectionStart = exitIndex - 1;
			while ( sectionStart >= 0 && !lines[ sectionStart ].StartsWith( ">> ", StringComparison.Ordinal ) )
				sectionStart--;

			if ( exitIndex - 1 > sectionStart )
				return lines[ exitIndex - 1 ];

			return string.Empty;
		}

		for ( var i = lines.Length - 1; i >= 0; i-- )
		{
			if ( lines[ i ].StartsWith( ">> ", StringComparison.Ordinal ) )
				continue;
			return lines[ i ];
		}

		return lines.LastOrDefault() ?? string.Empty;
	}

	public static async Task<int> RunElevatedCommandsAsync( IReadOnlyList<ElevatedCommand> commands, bool continueOnFailure = false )
	{
		var (exitCode, _) = await RunElevatedCommandsWithOutputAsync( commands, continueOnFailure: continueOnFailure );
		return exitCode;
	}

	public static async Task<bool> IsInPathAsync( string exe )
	{
		var (code, _) = await RunAsync( "where", exe );
		return code == 0;
	}

	public static bool TryExtractHostFromProxyAddress( string proxyAddress, out string host, out string error )
	{
		host = string.Empty;
		error = string.Empty;

		if ( string.IsNullOrWhiteSpace( proxyAddress ) )
		{
			error = "Proxy address is empty.";
			return false;
		}

		if ( !Uri.TryCreate( $"http://{proxyAddress.Trim()}", UriKind.Absolute, out var uri ) )
		{
			error = $"Proxy address is invalid: '{proxyAddress}'.";
			return false;
		}

		if ( uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 )
		{
			error = "Proxy address cannot contain path, query, or fragment.";
			return false;
		}

		host = uri.Host;
		if ( Uri.CheckHostName( host ) == UriHostNameType.Unknown )
		{
			error = $"Proxy host is invalid: '{host}'.";
			return false;
		}

		return true;
	}

	public static bool IsSafeServiceNameToken( string token ) =>
		!string.IsNullOrWhiteSpace( token ) && Regex.IsMatch( token, "^[A-Za-z0-9._-]+$" );

	public static bool IsSafeHostToken( string token ) =>
		!string.IsNullOrWhiteSpace( token ) && Uri.CheckHostName( token ) != UriHostNameType.Unknown;

	public static bool IsSafeV4HostToken( string token )
	{
		if ( string.IsNullOrWhiteSpace( token ) ) return false;

		if ( System.Net.IPAddress.TryParse( token, out var ip ) )
			return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

		return Regex.IsMatch( token, "^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$" );
	}

	public static bool IsSafeHostAliasToken( string token ) =>
		!string.IsNullOrWhiteSpace( token ) && Regex.IsMatch( token, "^[A-Za-z0-9._-]+$" );

	public static string ValidateWireGuardKeyOutput( string command, int exitCode, string output, string keyType )
	{
		if ( exitCode != 0 )
			throw new InvalidOperationException( $"{command} failed with exit code {exitCode}: {output}" );

		var key = output.Trim();
		if ( !WireGuardKeyRegex.IsMatch( key ) )
			throw new InvalidOperationException( $"{command} returned invalid {keyType} key output." );

		return key;
	}

	private static List<ElevatedCommand> NormalizeElevatedCommands( IReadOnlyList<ElevatedCommand> commands )
	{
		if ( commands.Count == 0 )
			throw new ArgumentException( "At least one elevated command is required.", nameof( commands ) );

		var normalized = new List<ElevatedCommand>( commands.Count );

		foreach ( var command in commands )
		{
			var exe = Path.GetFileName( command.FileName ).ToLowerInvariant();
			if ( !AllowedElevatedExecutables.TryGetValue( exe, out var resolvedPath ) )
				throw new InvalidOperationException( $"Elevated executable '{command.FileName}' is not allowlisted." );

			if ( !File.Exists( resolvedPath ) )
				throw new FileNotFoundException( $"Allowlisted elevated executable not found: {resolvedPath}" );

			if ( command.Arguments.Any( a => a.Contains( '\0' ) ) )
				throw new InvalidOperationException( "Command arguments cannot contain null characters." );

			normalized.Add( new ElevatedCommand
			{
				FileName = resolvedPath,
				Arguments = command.Arguments,
				SuccessExitCodes = command.SuccessExitCodes,
				Description = command.Description
			} );
		}

		return normalized;
	}

	private static async Task<(int ExitCode, string Output)> RunCoreAsync( ProcessStartInfo psi, string? stdin = null )
	{
		try
		{
			using var p = Process.Start( psi );
			if ( p == null ) return (-1, "Failed to start process.");

			var stdoutTask = p.StandardOutput.ReadToEndAsync();
			var stderrTask = p.StandardError.ReadToEndAsync();

			if ( stdin != null )
			{
				await p.StandardInput.WriteLineAsync( stdin );
				p.StandardInput.Close();
			}

			await p.WaitForExitAsync();
			await Task.WhenAll( stdoutTask, stderrTask );

			return (p.ExitCode, ( await stdoutTask + await stderrTask ).Trim());
		}
		catch ( Exception ex )
		{
			return (-1, ex.Message);
		}
	}
}