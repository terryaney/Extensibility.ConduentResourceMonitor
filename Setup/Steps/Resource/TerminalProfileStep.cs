namespace ConduentResourceMonitor.Setup.Steps.Resource;

public class TerminalProfileStep : ISetupStep
{
	private const string ProfileGuid = "{7c2d8c34-4f7a-4d1f-8f5d-8b7c4b6b9cda}";

	public string Title => "Add Windows Terminal Profile";
	public string Description =>
		"""
        Adds the resource provider profile to Windows Terminal.
        This profile runs pproxy automatically with a visible warning not to close the tab.

        Hard block: Windows Terminal must be installed. Install from the Microsoft Store if needed.
        """;
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool CanSkip => false;

	public Task<bool> IsApplicableAsync()
	{
		var path = FindSettingsPath();
		return Task.FromResult( path != null );
	}

	public Task<bool> IsAlreadyCompleteAsync()
	{
		var path = FindSettingsPath();
		if ( path == null ) return Task.FromResult( false );
		try
		{
			var content = File.ReadAllText( path );
			return Task.FromResult( content.Contains( ProfileGuid, StringComparison.OrdinalIgnoreCase ) );
		}
		catch
		{
			return Task.FromResult( false );
		}
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		var settingsPath = FindSettingsPath();
		if ( settingsPath == null )
			return new SetupStepResult( false, "Windows Terminal settings.json not found. Install Windows Terminal from the Microsoft Store first." );

		progress.Report( $"Editing: {settingsPath}" );
		var profileName = AppSettings.ResourceProviderTerminalProfileName.Replace( "'", "''" );
		var escapedSettingsPath = settingsPath.Replace( "'", "''" );

		var script = string.Join( "\r\n",
		[
			$"$settingsPath = '{escapedSettingsPath}'",
			$"$guid = '{ProfileGuid}'",
			"$content = Get-Content $settingsPath -Raw",
			"if ($content -match [regex]::Escape($guid)) {",
			"    Write-Output \"Profile already exists — no change made.\"",
			"    exit 0",
			"}",
			"$settings = $content | ConvertFrom-Json",
			"$profile = [pscustomobject]@{",
			"    guid = $guid",
			$"    name = \"{profileName}\"",
			"    commandline = 'pwsh.exe -NoExit -Command \"Write-Host ''DO NOT CLOSE this Terminal tab, it is needed for VPN support.'' -ForegroundColor Yellow; pproxy -l http://:8888\"'",
			"    background = \"#08082E\"",
			"    backgroundImage = \"C:\\BTR\\Extensibility\\PowerShell\\Icons\\vpn.png\"",
			"    backgroundImageAlignment = \"bottomRight\"",
			"    backgroundImageOpacity = 0.1",
			"    backgroundImageStretchMode = \"none\"",
			"    hidden = $false",
			"    icon = \"C:\\BTR\\Extensibility\\PowerShell\\Icons\\vpn.png\"",
			"    startingDirectory = \"C:\\BTR\\Extensibility\\PowerShell\"",
			"}",
			"$settings.profiles.list += $profile",
			"$settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath",
			"Write-Output \"Profile added successfully.\""
		] );

		var (code, output) = await ProcessHelper.RunPowerShellAsync( script );
		progress.Report( output );

		var ok = await IsAlreadyCompleteAsync();
		return new SetupStepResult( ok, ok ? "Terminal profile added." : $"Could not verify profile. Exit code: {code}" );
	}

	private static string? FindSettingsPath()
	{
		// Store version
		var localApp = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
		var storeGlob = Path.Combine( localApp, "Packages" );

		if ( Directory.Exists( storeGlob ) )
		{
			foreach ( var dir in Directory.GetDirectories( storeGlob, "Microsoft.WindowsTerminal*" ) )
			{
				var path = Path.Combine( dir, "LocalState", "settings.json" );
				if ( File.Exists( path ) ) return path;
			}
		}

		// Unpackaged / preview
		var altPath = Path.Combine( localApp, "Microsoft", "Windows Terminal", "settings.json" );
		if ( File.Exists( altPath ) ) return altPath;
		return null;
	}
}