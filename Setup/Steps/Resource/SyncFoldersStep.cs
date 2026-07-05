namespace ConduentResourceMonitor.Setup.Steps.Resource;

// Captures the OneDrive bridge folder pair. Pinning and hydration-waiting happen automatically at
// runtime once the monitor is running (see SyncEngine) — no Explorer action required here.
public class SyncFoldersStep( SetupContext ctx ) : ISetupStep
{
	public string Title => "Configure Sync Folders";
	public string Description =>
		"Optional: mirrors two local folders — one under each OneDrive account's root — so files flow between the Hub and Resource accounts.\r\n\r\n" +
		"Leave both paths blank to skip folder sync.\r\n\r\n" +
		"Both folders are pinned and hydration-checked automatically by the running monitor — no Explorer action is required.";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public IReadOnlyList<SetupInput> Inputs => [ctx.HubSyncPathInput(), ctx.ResourceSyncPathInput(), ctx.SyncIgnorePatternsInput()];

	public Task<bool> IsAlreadyCompleteAsync()
	{
		var hubSet = !string.IsNullOrWhiteSpace( ctx.HubSyncPath );
		var resSet = !string.IsNullOrWhiteSpace( ctx.ResourceSyncPath );
		if ( !hubSet && !resSet )
			return Task.FromResult( true ); // feature off

		return Task.FromResult(
			hubSet && resSet &&
			Directory.Exists( ctx.HubSyncPath ) && Directory.Exists( ctx.ResourceSyncPath ) &&
			!ConflictingPaths( ctx.HubSyncPath, ctx.ResourceSyncPath )
		);
	}

	public Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		var hubSet = !string.IsNullOrWhiteSpace( ctx.HubSyncPath );
		var resSet = !string.IsNullOrWhiteSpace( ctx.ResourceSyncPath );

		if ( !hubSet && !resSet )
			return Task.FromResult( new SetupStepResult( true, "Skipped — no sync folders configured." ) );

		if ( hubSet != resSet )
			return Task.FromResult( new SetupStepResult( false, "Set both sync paths, or blank both to disable folder sync." ) );

		if ( ConflictingPaths( ctx.HubSyncPath, ctx.ResourceSyncPath ) )
			return Task.FromResult( new SetupStepResult( false, "Sync paths must not be the same folder or nested inside each other." ) );

		if ( !Directory.Exists( ctx.HubSyncPath ) || !Directory.Exists( ctx.ResourceSyncPath ) )
			return Task.FromResult( new SetupStepResult( false, "Both sync folders must exist." ) );

		return Task.FromResult( new SetupStepResult( true, "Sync folders configured." ) );
	}

	private static bool ConflictingPaths( string hubPath, string resPath )
	{
		var hubFull = Path.TrimEndingDirectorySeparator( Path.GetFullPath( hubPath ) );
		var resFull = Path.TrimEndingDirectorySeparator( Path.GetFullPath( resPath ) );
		return hubFull.Equals( resFull, StringComparison.OrdinalIgnoreCase ) ||
			hubFull.StartsWith( resFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase ) ||
			resFull.StartsWith( hubFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase );
	}
}
