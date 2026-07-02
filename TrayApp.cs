using System.Drawing.Drawing2D;
using ConduentResourceMonitor.Checks;
using ConduentResourceMonitor.Repairs;
using ConduentResourceMonitor.Services;

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

	public TrayApp( AppSettings settings, bool showLog, bool repairOnStart = false )
	{
		_settings = settings;
		_mode = settings.AppMode!.Value; // validated non-null before TrayApp is constructed

		_greenIcon = CreateCircleIcon( Color.LimeGreen );
		_redIcon = CreateCircleIcon( Color.Red );

		_logForm = new LogForm();

		if ( _mode == AppMode.Resource )
		{
			_proxyServer = new ProxyServerService();
			_proxyServer.Start();
			if ( _proxyServer.LastError != null )
				_logForm.AppendLine( $"[{DateTime.Now:HH:mm:ss}] VPN Proxy failed to start: {_proxyServer.LastError}" );
		}
		else
		{
			_pacServer = new PacServerService( settings );
			_pacServer.Start();
			if ( _pacServer.LastError != null )
				_logForm.AppendLine( $"[{DateTime.Now:HH:mm:ss}] PAC Web Server failed to start: {_pacServer.LastError}" );
		}

		var checks = BuildChecks( _mode, settings );
		_repairs = BuildRepairs( _mode, settings, checks );

		_monitor = new MonitorService( checks, settings.CheckIntervalSeconds );
		_monitor.ResultsUpdated += OnResultsUpdated;
		_monitor.CheckFailed += OnCheckFailed;

		_tray = new NotifyIcon
		{
			Icon = _greenIcon,
			Text = $"{_mode} Monitor - Starting...",
			Visible = true,
			ContextMenuStrip = BuildContextMenu()
		};

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
		var allOk = results.All( r => r.Ok );
		_tray.Icon = allOk ? _greenIcon : _redIcon;

		var parts = results.Select( r => $"{r.Name}: {( r.Ok ? "OK" : "FAIL" )}" );
		_tray.Text = string.Join( Environment.NewLine, parts );

		var ts = DateTime.Now.ToString( "HH:mm:ss" );
		foreach ( var r in results )
			_logForm.AppendLine( $"[{ts}] {r.Name}: {( r.Ok ? "OK" : "FAIL" )} ({r.Detail})" );

		foreach ( var r in results )
		{
			if ( r.Ok )
			{
				_repairAttempts[ r.Name ] = 0;
				continue;
			}

			var attempts = _repairAttempts.GetValueOrDefault( r.Name, 0 );
			if ( attempts >= 2 ) continue;

			var repair = _repairs.FirstOrDefault( rp => rp.TargetCheckName == r.Name && !rp.RequiresElevation );
			if ( repair == null ) continue;
			if ( !QueueRepair( repair ) ) continue;

			_repairAttempts[ r.Name ] = attempts + 1;
			_logForm.AppendLine( $"[{ts}] AUTO-REPAIR ({attempts + 1}/2): {repair.Label}" );
		}
	}

	private void OnCheckFailed( CheckResult result )
	{
		_tray.ShowBalloonTip(
			_settings.NotifyTimeoutMs,
			$"{_mode} Monitor - {result.Name} Failed",
			result.Detail,
			ToolTipIcon.Warning
		);
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

		var showLog = new ToolStripMenuItem( "Show Log" );
		showLog.Click += ( _, _ ) => { _logForm.Show(); _logForm.BringToFront(); };
		menu.Items.Add( showLog );

		var settingsItem = new ToolStripMenuItem( "Settings" );
		settingsItem.Click += ( _, _ ) =>
		{
			var oldPort = _settings.PacPort;
			var oldDir = _settings.PacDirectory;
			using var form = new SettingsForm( _settings, allowModeChange: false );
			if ( form.ShowDialog() == DialogResult.OK )
			{
				_monitor.UpdateInterval( _settings.CheckIntervalSeconds );
				if ( _settings.PacPort != oldPort || _settings.PacDirectory != oldDir )
					_pacServer?.Restart();
			}
		};
		menu.Items.Add( settingsItem );

		menu.Items.Add( new ToolStripSeparator() );

		var exit = new ToolStripMenuItem( "Exit" );
		exit.Click += ( _, _ ) => Shutdown();
		menu.Items.Add( exit );
	}

	private void Shutdown()
	{
		_monitor.Stop();
		_pacServer?.Stop();
		_proxyServer?.Stop();
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

	private void AppendRepairLog( string line )
	{
		var ts = DateTime.Now.ToString( "HH:mm:ss" );
		_logForm.AppendLine( $"[{ts}] {line}" );
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
			_logForm.Dispose();
			_greenIcon.Dispose();
			_redIcon.Dispose();
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
}