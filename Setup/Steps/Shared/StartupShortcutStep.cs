namespace ConduentResourceMonitor.Setup.Steps.Shared;

public class StartupShortcutStep( SetupMode mode, SetupContext ctx ) : ISetupStep
{
	private readonly SetupMode _mode = mode;
	private readonly SetupContext _ctx = ctx;

	public string Title => "Create Startup Shortcut";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool CanSkip => false;

	public string Description => _mode switch
	{
		SetupMode.Hub => "Creates a startup shortcut that launches the Resource Monitor in Hub mode with --repair-on-start.",
		SetupMode.Travel => "Creates a startup shortcut that launches the Resource Monitor in Travel mode.",
		SetupMode.Resource => "Creates a startup shortcut that launches the pproxy terminal profile via Windows Terminal.",
		_ => ""
	};

	private string ShortcutName => _mode switch
	{
		SetupMode.Hub => "Conduent Resource Monitor - Hub.lnk",
		SetupMode.Travel => "Conduent Resource Monitor - Travel.lnk",
		SetupMode.Resource => "Conduent-Resource - Resource Provider.lnk",
		_ => "Conduent.lnk"
	};

	private static string StartupDir => Environment.GetFolderPath( Environment.SpecialFolder.Startup );
	private string ShortcutPath => Path.Combine( StartupDir, ShortcutName );

	public Task<bool> IsAlreadyCompleteAsync() => Task.FromResult( File.Exists( ShortcutPath ) );

	public Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		try
		{
			progress.Report( $"Creating shortcut: {ShortcutPath}" );
			dynamic shell = Activator.CreateInstance( Type.GetTypeFromProgID( "WScript.Shell" )! )!;
			dynamic lnk = shell.CreateShortcut( ShortcutPath );

			if ( _mode == SetupMode.Resource )
			{
				lnk.TargetPath = "wt.exe";
				lnk.Arguments = "-p \"Conduent-Resource - Resource Provider\"";
				lnk.WorkingDirectory = @"C:\BTR\Extensibility\PowerShell";
				lnk.IconLocation = @"C:\BTR\Extensibility\PowerShell\Icons\vpn.png";
			}
			else
			{
				var exePath = Path.Combine( AppContext.BaseDirectory, "ConduentResourceMonitor.exe" );
				var args = _mode == SetupMode.Hub ? "--mode Hub --repair-on-start" : "--mode Travel";
				lnk.TargetPath = exePath;
				lnk.Arguments = args;
				lnk.WorkingDirectory = AppContext.BaseDirectory;
				lnk.IconLocation = @"C:\BTR\Extensibility\PowerShell\Icons\vpn.png";
			}

			lnk.Save();

			progress.Report( "Shortcut created." );
			return Task.FromResult( new SetupStepResult( true, $"Shortcut created at:\r\n{ShortcutPath}" ) );
		}
		catch ( Exception ex )
		{
			return Task.FromResult( new SetupStepResult( false, ex.Message ) );
		}
	}
}