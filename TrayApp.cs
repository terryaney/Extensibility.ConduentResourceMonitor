using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using ConduentResourceMonitor.Checks;
using ConduentResourceMonitor.Repairs;
using ConduentResourceMonitor.Services;
using ConduentResourceMonitor.Services.Sync;

namespace ConduentResourceMonitor;

public class TrayApp : ApplicationContext
{
	private readonly AppMode _mode;
	private readonly AppSettings _settings;
	private readonly NotifyIcon _tray;
	private readonly MonitorService _monitor;
	private readonly PacServerService? _pacServer;
	private readonly ProxyServerService? _proxyServer;
	private readonly List<IRepair> _repairs;
	private readonly LogForm _logForm;
	private readonly Dictionary<string, int> _repairAttempts = [];
	private readonly HashSet<string> _repairsInFlight = [];
	private readonly object _repairLock = new();
	private readonly Icon _greenIcon;
	private readonly Icon _redIcon;
	private readonly Icon _greenSyncIcon;
	private FolderSyncService? _sync;
	private readonly SyncLog _monitorLog;
	private readonly Dictionary<string, bool> _lastCheckOk = new( StringComparer.OrdinalIgnoreCase );
	private SyncStatus? _syncStatus;
	private bool _lastAllOk = true;
	private string _checkLinesText;
	private bool _syncErrorBalloonShown;
	private readonly HashSet<string> _notifiedHydrating = new( StringComparer.OrdinalIgnoreCase );
	private bool _shuttingDown;

	public TrayApp( AppSettings settings, bool showLog, bool repairOnStart = false )
	{
		_settings = settings;
		_mode = settings.AppMode!.Value; // validated non-null before TrayApp is constructed

		_greenIcon = CreateCircleIcon( Color.LimeGreen );
		_redIcon = CreateCircleIcon( Color.Red );
		_greenSyncIcon = LoadSyncIcon();
		_checkLinesText = $"{_mode} Monitor - Starting...";

		_logForm = new LogForm();
		_monitorLog = new SyncLog( Path.Combine( AppContext.BaseDirectory, "monitor.log" ), mirror: _logForm.AppendLine );

		if ( _mode == AppMode.Resource )
		{
			_proxyServer = new ProxyServerService();
			_proxyServer.Start();
			if ( _proxyServer.LastError != null )
				_monitorLog.Line( $"VPN Proxy failed to start: {_proxyServer.LastError}" );
		}
		else
		{
			_pacServer = new PacServerService( settings );
			_pacServer.Start();
			if ( _pacServer.LastError != null )
				_monitorLog.Line( $"PAC Web Server failed to start: {_pacServer.LastError}" );
		}

		var checks = BuildChecks( _mode, settings );
		_repairs = BuildRepairs( _mode, settings, checks );

		_monitor = new MonitorService( checks, settings.CheckIntervalSeconds );
		_monitor.ResultsUpdated += OnResultsUpdated;

		_tray = new NotifyIcon
		{
			Icon = _greenIcon,
			Text = $"{_mode} Monitor - Starting...",
			Visible = true,
			ContextMenuStrip = BuildContextMenu()
		};

		if ( _mode == AppMode.Resource && settings.SyncConfigured )
			StartSyncService();

		if ( showLog ) _logForm.Show();

		if ( repairOnStart && _mode == AppMode.Hub )
		{
			var repair = _repairs.OfType<PortProxyRepair>().FirstOrDefault();
			if ( repair != null ) _ = RunStartupPortProxyRepairAsync( repair );
		}

		_ = _monitor.Start();
	}

	private static bool IsLanOnlyHub( AppMode mode, AppSettings settings ) =>
		mode == AppMode.Hub && settings.SkipWireGuard;

	private static IReadOnlyList<ICheck> BuildChecks( AppMode mode, AppSettings settings ) => mode switch
	{
		AppMode.Hub when IsLanOnlyHub( mode, settings ) =>
		[
			new ProxyCheck( "Resource Proxy Connectivity", settings ),
			new PortForwardCheck( "Port Proxy / Forwarding", "localhost", 8888, 13389 ),
			new PacServerCheck( settings )
		],
		AppMode.Hub =>
		[
			new ProxyCheck( "Resource VPN", settings ),
			new PortForwardCheck( "Port Proxy / Forwarding", "localhost", 8888, 13389 ),
			new PacServerCheck( settings ),
			new WireGuardCheck( settings )
		],
		AppMode.Travel =>
		[
			new ProxyCheck( "Resource VPN", settings ),
			new PortForwardCheck( "Resource RDP", "conduent-resource", 13389 ),
			new PacServerCheck( settings ),
			new WireGuardCheck( settings )
		],
		AppMode.Resource =>
		[
			new ProxyCheck( "VPN Enabled", settings ),
			new PortForwardCheck( "VPN Proxy", "localhost", ProxyServerService.Port )
		],
		_ => throw new ArgumentOutOfRangeException( nameof( mode ) )
	};

	private List<IRepair> BuildRepairs( AppMode mode, AppSettings settings, IReadOnlyList<ICheck> checks )
	{
		var proxyCheck = checks.First( i => i is ProxyCheck );
		var repairs = new List<IRepair>();

		switch ( mode )
		{
			case AppMode.Hub:
				repairs.Add( IsLanOnlyHub( mode, settings )
					? new ResourceVpnRepair( proxyCheck, isLanOnly: true )
					: new ResourceVpnRepair( proxyCheck ) );
				repairs.Add( new PortProxyRepair( settings, checks.First( i => i is PortForwardCheck ) ) );
				if ( !IsLanOnlyHub( mode, settings ) )
					repairs.Add( new WireGuardRepair( settings, checks.First( i => i is WireGuardCheck ) ) );
				repairs.Add( new PacServerRepair( _pacServer!, checks.First( i => i is PacServerCheck ) ) );
				break;
			case AppMode.Travel:
				repairs.Add( new ResourceVpnRepair( proxyCheck ) );
				repairs.Add( new WireGuardRepair( settings, checks.First( i => i is WireGuardCheck ) ) );
				repairs.Add( new PacServerRepair( _pacServer!, checks.First( i => i is PacServerCheck ) ) );
				break;
			case AppMode.Resource:
				repairs.Add( new LocalVpnRepair( proxyCheck ) );
				repairs.Add( new ProxyServerRepair( _proxyServer!, checks.First( i => i is PortForwardCheck ) ) );
				break;
		}

		return repairs;
	}

	private void OnResultsUpdated( IReadOnlyList<CheckResult> results )
	{
		_lastAllOk = results.All( r => r.Ok );
		_checkLinesText = string.Join( Environment.NewLine, results.Select( r => $"{r.Name}: {( r.Ok ? "OK" : "FAIL" )}" ) );
		UpdateTrayPresentation();

		foreach ( var r in results )
			_monitorLog.Line( $"{r.Name}: {( r.Ok ? "OK" : "FAIL" )} ({r.Detail})" );

		foreach ( var r in results )
		{
			if ( r.Ok )
			{
				_repairAttempts[ r.Name ] = 0;
				_lastCheckOk[ r.Name ] = true;
				continue;
			}

			var wasKnownOk = _lastCheckOk.TryGetValue( r.Name, out var wasOk ) && wasOk;
			if ( wasKnownOk && !_shuttingDown )
				_tray.ShowBalloonTip(
					_settings.NotifyTimeoutMs,
					$"{_mode} Monitor - {r.Name} Failed",
					$"{r.Name} is no longer connected. See monitor for possible fixes.",
					ToolTipIcon.Warning
				);

			_lastCheckOk[ r.Name ] = false;

			var attempts = _repairAttempts.GetValueOrDefault( r.Name, 0 );
			if ( attempts >= 2 ) continue;

			var repair = _repairs.FirstOrDefault( rp => rp.TargetCheckName == r.Name && !rp.RequiresElevation );
			if ( repair == null ) continue;
			if ( !QueueRepair( repair ) ) continue;

			_repairAttempts[ r.Name ] = attempts + 1;
			_monitorLog.Line( $"AUTO-REPAIR ({attempts + 1}/2): {repair.Label}" );
		}
	}

	private void StartSyncService()
	{
		_sync = new FolderSyncService( _settings, _logForm.AppendLine );
		_sync.StatusChanged += OnSyncStatus;
		_sync.Start();
	}

	private void RestartSyncService()
	{
		_sync?.Stop();
		_sync?.Dispose();
		_sync = null;
		_syncStatus = null;
		_syncErrorBalloonShown = false;
		_notifiedHydrating.Clear();
		_lastCheckOk.Clear();
		if ( _mode == AppMode.Resource && _settings.SyncConfigured )
			StartSyncService();
		UpdateTrayPresentation();
	}

	private void OnSyncStatus( SyncStatus status )
	{
		_syncStatus = status;

		// One balloon per healthy→erroring transition; recovery re-arms it
		if ( status.ErrorCount > 0 && !_syncErrorBalloonShown )
		{
			_syncErrorBalloonShown = true;
			if ( !_shuttingDown )
				_tray.ShowBalloonTip(
					_settings.NotifyTimeoutMs,
					$"{_mode} Monitor - Sync Errors",
					status.LastError ?? $"{status.ErrorCount} sync errors — see sync.log.",
					ToolTipIcon.Warning
				);
		}
		else if ( status.ErrorCount == 0 )
			_syncErrorBalloonShown = false;

		// One balloon per folder the first time it appears pending hydration; a folder dropping out
		// of the set re-arms it so a later re-occurrence (e.g. a different large folder) notifies again.
		var stillPending = new HashSet<string>( status.PendingHydration, StringComparer.OrdinalIgnoreCase );
		foreach ( var dir in status.PendingHydration )
		{
			if ( _notifiedHydrating.Add( dir ) && !_shuttingDown )
				_tray.ShowBalloonTip(
					_settings.NotifyTimeoutMs,
					$"{_mode} Monitor - Sync Waiting",
					$"'{dir}' is still downloading from OneDrive — sync will start automatically once it finishes.",
					ToolTipIcon.Info
				);
		}
		_notifiedHydrating.RemoveWhere( dir => !stillPending.Contains( dir ) );

		UpdateTrayPresentation();
	}

	// Single owner of icon + tooltip so check results and sync status can't fight over them.
	// Glyph = arrows only while a reconcile pass is actively in progress (always green in that
	// state); otherwise a plain circle colored by the monitored checks. Sync errors alone never
	// affect color.
	private void UpdateTrayPresentation()
	{
		var reconciling = _syncStatus is { Reconciling: true };
		_tray.Icon = reconciling
			? _greenSyncIcon
			: ( _lastAllOk ? _greenIcon : _redIcon );

		var text = _checkLinesText;
		if ( _sync != null && _syncStatus != null )
		{
			text += Environment.NewLine + BuildSyncLine( _syncStatus );
			if ( _syncStatus.TrashFileCount > 0 )
				text += Environment.NewLine + $"SyncTrash: {_syncStatus.TrashFileCount} files";
		}

		// NotifyIcon.Text throws over 127 chars — truncating also fixes a latent bug where many
		// long check names could previously blow the limit
		_tray.Text = text.Length <= 127 ? text : text[ ..127 ];
	}

	private static string BuildSyncLine( SyncStatus status )
	{
		if ( status.Paused ) return "Sync: Paused";
		if ( status.ErrorCount > 0 ) return $"Sync: {status.ErrorCount} errors";
		if ( status.PendingHydration.Count > 0 ) return $"Sync: waiting on OneDrive ({status.PendingHydration.Count} folders)";
		return status.LastSyncLocal.HasValue ? $"Sync: OK (last {status.LastSyncLocal:HH:mm})" : "Sync: OK";
	}

	private ContextMenuStrip BuildContextMenu()
	{
		var menu = new ContextMenuStrip();
		menu.Opening += ( _, _ ) => RebuildMenu( menu );

		return menu;
	}

	private void RebuildMenu( ContextMenuStrip menu )
	{
		menu.Items.Clear();

		var failing = _monitor.LastResults
			.Where( r => !r.Ok )
			.Select( r => r.Name )
			.ToHashSet();

		var fixItems = _repairs.Where( r => failing.Contains( r.TargetCheckName ) ).ToList();
		foreach ( var repair in fixItems )
		{
			var r = repair;
			var item = new ToolStripMenuItem( $"Fix: {r.Label}" );
			item.Click += ( _, _ ) => _ = QueueRepair( r );
			menu.Items.Add( item );
		}
		if ( fixItems.Count > 0 )
			menu.Items.Add( new ToolStripSeparator() );

		if ( _sync != null )
		{
			var paused = _syncStatus?.Paused ?? _settings.SyncPaused;
			var pauseItem = new ToolStripMenuItem( paused ? "Resume Syncing" : "Pause Syncing" );
			pauseItem.Click += ( _, _ ) =>
			{
				_settings.SyncPaused = !paused;
				_settings.Save();
				if ( paused ) _sync.Resume();
				else _sync.Pause();
			};
			menu.Items.Add( pauseItem );

			var trashCount = _sync.GetTrashFileCount();
			var purgeItem = new ToolStripMenuItem( $"Purge Sync Trash ({trashCount} files)" ) { Enabled = trashCount > 0 };
			purgeItem.Click += ( _, _ ) =>
			{
				var confirm = MessageBox.Show(
					$"Permanently delete {trashCount} file(s) from SyncTrash?",
					"Conduent Resource Monitor",
					MessageBoxButtons.YesNo,
					MessageBoxIcon.Warning );
				if ( confirm == DialogResult.Yes ) _sync.PurgeTrash();
			};
			menu.Items.Add( purgeItem );

			menu.Items.Add( new ToolStripSeparator() );
		}

		var showLog = new ToolStripMenuItem( "Show Log" );
		showLog.Click += ( _, _ ) => { _logForm.Show(); _logForm.BringToFront(); };
		menu.Items.Add( showLog );

		var settingsItem = new ToolStripMenuItem( "Settings" );
		settingsItem.Click += ( _, _ ) =>
		{
			var oldPort = _settings.PacPort;
			var oldDir = _settings.PacDirectory;
			var oldHubSync = _settings.HubSyncPath;
			var oldResSync = _settings.ResourceSyncPath;
			var oldIgnore = _settings.SyncIgnorePatterns;
			using var form = new SettingsForm( _settings, allowModeChange: false );
			if ( form.ShowDialog() == DialogResult.OK )
			{
				_monitor.UpdateInterval( _settings.CheckIntervalSeconds );
				if ( _settings.PacPort != oldPort || _settings.PacDirectory != oldDir )
					_pacServer?.Restart();
				if ( _settings.HubSyncPath != oldHubSync || _settings.ResourceSyncPath != oldResSync || _settings.SyncIgnorePatterns != oldIgnore )
					RestartSyncService();
			}
		};
		menu.Items.Add( settingsItem );

		if ( _pacServer != null )
		{
			var viewPac = new ToolStripMenuItem( "View PAC File" ) { Enabled = File.Exists( Path.Combine( _settings.PacDirectory, _settings.PacFileName ) ) };
			viewPac.Click += ( _, _ ) => OpenPacFileInEditor();
			menu.Items.Add( viewPac );
		}

		menu.Items.Add( new ToolStripSeparator() );

		var exit = new ToolStripMenuItem( "Exit" );
		exit.Click += ( _, _ ) => Shutdown();
		menu.Items.Add( exit );
	}

	private void Shutdown()
	{
		_shuttingDown = true;
		_monitor.Stop();
		_pacServer?.Stop();
		_proxyServer?.Stop();
		_sync?.Stop();
		_tray.Visible = false;
		_tray.Dispose();
		Application.Exit();
	}

	private async Task RunStartupPortProxyRepairAsync( PortProxyRepair repair )
	{
		var key = repair.TargetCheckName;
		if ( !TryStartRepair( key ) )
		{
			AppendRepairLog( $"Skipped startup repair '{repair.Label}' because one is already in progress." );
			return;
		}

		try
		{
			await repair.ExecuteAsync( startupDelay: false, logLine: AppendRepairLog );
		}
		catch ( Exception ex )
		{
			AppendRepairLog( $"Startup repair '{repair.Label}' failed: {ex.Message}" );
		}
		finally
		{
			EndRepair( key );
		}
	}

	private async Task RunRepairAsync( IRepair repair )
	{
		try
		{
			await repair.ExecuteAsync( AppendRepairLog );
		}
		catch ( Exception ex )
		{
			AppendRepairLog( $"{repair.Label} failed: {ex.Message}" );
		}
		finally
		{
			EndRepair( repair.TargetCheckName );
		}
	}

	private bool QueueRepair( IRepair repair )
	{
		if ( !TryStartRepair( repair.TargetCheckName ) )
		{
			AppendRepairLog( $"Skipped '{repair.Label}' because a repair for '{repair.TargetCheckName}' is already in progress." );
			return false;
		}

		_ = RunRepairAsync( repair );
		return true;
	}

	private void OpenPacFileInEditor()
	{
		var path = Path.Combine( _settings.PacDirectory, _settings.PacFileName );
		try
		{
			Process.Start( new ProcessStartInfo { FileName = "code", Arguments = $"\"{path}\"", UseShellExecute = true } );
		}
		catch ( Exception ex )
		{
			AppendRepairLog( $"Failed to open PAC file in VS Code: {ex.Message}" );
		}
	}

	private void AppendRepairLog( string line )
	{
		_monitorLog.Line( line );
	}

	private bool TryStartRepair( string targetCheckName )
	{
		lock ( _repairLock )
			return _repairsInFlight.Add( targetCheckName );
	}

	private void EndRepair( string targetCheckName )
	{
		lock ( _repairLock )
			_ = _repairsInFlight.Remove( targetCheckName );
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing )
		{
			_monitor.Stop();
			_pacServer?.Stop();
			_proxyServer?.Stop();
			_sync?.Dispose();
			_logForm.Dispose();
			_greenIcon.Dispose();
			_redIcon.Dispose();
			_greenSyncIcon.Dispose();
		}
		base.Dispose( disposing );
	}

	private static Icon CreateCircleIcon( Color color )
	{
		using var bmp = new Bitmap( 32, 32 );
		using ( var g = Graphics.FromImage( bmp ) )
		using ( var brush = new SolidBrush( color ) )
		{
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.FillEllipse( brush, 2, 2, 28, 28 );
		}
		var hIcon = bmp.GetHicon();
		return Icon.FromHandle( hIcon );
	}

	// Windows' own imageres.dll refresh glyph (icon index 229) — embedded as a project resource
	// rather than hand-drawn, since it reads more polished than GDI+ arcs at tray size.
	private static Icon LoadSyncIcon()
	{
		using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream( "ConduentResourceMonitor.Resources.SyncIcon.ico" )!;
		return new Icon( stream );
	}
}