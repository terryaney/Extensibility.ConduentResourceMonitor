namespace ConduentResourceMonitor;

public class SettingsForm : Form
{
	private readonly AppSettings _settings;
	private readonly Dictionary<string, TextBox> _fields = [];
	private readonly ComboBox _modeCombo;
	private bool _tunnelNameModified;
	private bool _settingTunnelName;

	public SettingsForm( AppSettings settings, bool allowModeChange = true, IReadOnlyList<string>? validationErrors = null )
	{
		_settings = settings;

		// Track whether tunnel name has been customized away from the "{Mode}-Tunnel" default
		var defaultTunnelName = settings.Mode != null ? $"{settings.Mode}-Tunnel" : null;
		_tunnelNameModified = defaultTunnelName != null && settings.TunnelName != defaultTunnelName;

		Text = "Settings";
		StartPosition = FormStartPosition.CenterScreen;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;

		// --- Button panel (Dock.Bottom) ---
		var btnPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			FlowDirection = FlowDirection.RightToLeft,
			Height = 44,
			Padding = new Padding( 8, 6, 8, 6 )
		};
		var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
		var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
		btnOk.Click += ( _, _ ) => SaveSettings();
		btnPanel.Controls.AddRange( btnCancel, btnOk );

		// --- Error panel (Dock.Top) ---
		Panel? errorPanel = null;
		if ( validationErrors?.Count > 0 )
		{
			errorPanel = new Panel
			{
				Dock = DockStyle.Top,
				BackColor = Color.FromArgb( 255, 235, 235 ),
				Padding = new Padding( 12, 8, 12, 8 ),
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink
			};
			errorPanel.Controls.Add( new Label
			{
				Text = "The following issues must be resolved before the monitor can start:\r\n" +
					   string.Join( "\r\n", validationErrors.Select( e => $"  • {e}" ) ),
				ForeColor = Color.DarkRed,
				AutoSize = true
			} );
		}

		// --- Field layout (Dock.Fill) ---
		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 9,
			Padding = new Padding( 12, 12, 12, 4 ),
			AutoSize = false
		};
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );

		// Row 0: Mode dropdown
		layout.Controls.Add( new Label { Text = "Mode", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding( 0, 6, 8, 2 ) } );
		_modeCombo = new ComboBox
		{
			DropDownStyle = ComboBoxStyle.DropDownList,
			Dock = DockStyle.Fill,
			Margin = new Padding( 0, 3, 0, 3 ),
			Enabled = allowModeChange
		};
		
		_modeCombo.Items.AddRange( "Hub", "Travel" );
		
		if ( settings.Mode != null ) _modeCombo.SelectedItem = settings.Mode;
		_modeCombo.SelectedIndexChanged += OnModeChanged;
		
		layout.Controls.Add( _modeCombo );

		// Rows 1-7: text fields
		AddField( layout, "Check URL", nameof( AppSettings.CheckUrl ), settings.CheckUrl );
		AddField( layout, "Proxy Address", nameof( AppSettings.ProxyAddress ), settings.ProxyAddress );
		AddField( layout, "Tunnel Name", nameof( AppSettings.TunnelName ), settings.TunnelName );
		AddField( layout, "PAC Directory", nameof( AppSettings.PacDirectory ), settings.PacDirectory );
		AddField( layout, "PAC Port", nameof( AppSettings.PacPort ), settings.PacPort.ToString() );
		AddField( layout, "Check Interval (s)", nameof( AppSettings.CheckIntervalSeconds ), settings.CheckIntervalSeconds.ToString() );
		AddField( layout, "Notify Timeout (ms)", nameof( AppSettings.NotifyTimeoutMs ), settings.NotifyTimeoutMs.ToString() );

		// Watch for manual edits to TunnelName so we stop auto-updating it
		_fields[ nameof( AppSettings.TunnelName ) ].TextChanged += ( _, _ ) =>
		{
			if ( !_settingTunnelName ) _tunnelNameModified = true;
		};

		// Row 8: note
		var note = new Label
		{
			Text = "Proxy Address changes take effect on next check.",
			ForeColor = SystemColors.GrayText,
			AutoSize = false,
			Dock = DockStyle.Fill,
			Height = 24
		};
		layout.Controls.Add( note, 0, 8 );
		layout.SetColumnSpan( note, 2 );

		// Add to form in docking order: Fill first, then Top, then Bottom
		// (WinForms processes Z-order last-to-first: Bottom docked first, Top second, Fill last)
		Controls.Add( layout );
		if ( errorPanel != null ) Controls.Add( errorPanel );
		Controls.Add( btnPanel );

		AcceptButton = btnOk;
		CancelButton = btnCancel;

		var errorHeight = validationErrors?.Count > 0 ? 20 + ( validationErrors.Count + 1 ) * 18 : 0;
		Size = new Size( 580, 365 + errorHeight );
		MinimumSize = Size;
	}

	private void OnModeChanged( object? sender, EventArgs e )
	{
		if ( _tunnelNameModified ) return;
		if ( _modeCombo.SelectedItem is string mode )
		{
			_settingTunnelName = true;
			_fields[ nameof( AppSettings.TunnelName ) ].Text = $"{mode}-Tunnel";
			_settingTunnelName = false;
		}
	}

	private void AddField( TableLayoutPanel layout, string label, string key, string value )
	{
		var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding( 0, 6, 8, 2 ) };
		var tb = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding( 0, 3, 0, 3 ) };
		_fields[ key ] = tb;
		layout.Controls.Add( lbl );
		layout.Controls.Add( tb );
	}

	private void SaveSettings()
	{
		if ( _modeCombo.SelectedItem != null ) _settings.Mode = _modeCombo.SelectedItem.ToString();
		_settings.CheckUrl = _fields[ nameof( AppSettings.CheckUrl ) ].Text.Trim();
		_settings.ProxyAddress = _fields[ nameof( AppSettings.ProxyAddress ) ].Text.Trim();
		_settings.TunnelName = _fields[ nameof( AppSettings.TunnelName ) ].Text.Trim();
		_settings.PacDirectory = _fields[ nameof( AppSettings.PacDirectory ) ].Text.Trim();
		if ( int.TryParse( _fields[ nameof( AppSettings.PacPort ) ].Text, out var port ) ) _settings.PacPort = port;
		if ( int.TryParse( _fields[ nameof( AppSettings.CheckIntervalSeconds ) ].Text, out var interval ) ) _settings.CheckIntervalSeconds = interval;
		if ( int.TryParse( _fields[ nameof( AppSettings.NotifyTimeoutMs ) ].Text, out var timeout ) ) _settings.NotifyTimeoutMs = timeout;
		_settings.Save();
	}
}