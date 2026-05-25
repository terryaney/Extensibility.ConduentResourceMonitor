namespace ConduentResourceMonitor.Setup;

public class SetupPreflightForm : Form
{
	private readonly SetupMode _mode;
	private readonly SetupContext _ctx;
	private readonly Label _errorLabel;
	private readonly Button _btnOk = null!;

	// Hub fields
	private TextBox? _tbResourceIp;
	private TextBox? _tbHubPublicIp;
	private TextBox? _tbConfDir;
	private TextBox? _tbTravelNames;
	private CheckBox? _cbSkipWg;

	// Travel fields
	private TextBox? _tbConfFile;

	public SetupPreflightForm( SetupMode mode, SetupContext ctx )
	{
		_mode = mode;
		_ctx = ctx;

		// Load existing settings if available to pre-fill form
		var settings = AppSettings.Load();

		if ( !string.IsNullOrEmpty( settings.PacDirectory ) && ctx.ConfDirectory == @"C:\BTR\Extensibility\ConduentResource" )
			ctx.ConfDirectory = settings.PacDirectory;

		if ( !string.IsNullOrEmpty( settings.ResourceStaticIp ) && string.IsNullOrEmpty( ctx.ResourceStaticIp ) )
			ctx.ResourceStaticIp = settings.ResourceStaticIp;

		Text = $"Conduent Resource Setup — {mode} Configuration";
		StartPosition = FormStartPosition.CenterScreen;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		AutoSize = false;

		_errorLabel = new Label
		{
			ForeColor = Color.DarkRed,
			Dock = DockStyle.Top,
			AutoSize = false,
			Padding = new Padding( 12, 6, 12, 4 ),
			Visible = false
		};

		var btnPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			FlowDirection = FlowDirection.RightToLeft,
			Height = 44,
			Padding = new Padding( 8, 6, 8, 6 )
		};

		var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

		_btnOk = new Button { Text = "Continue →", AutoSize = true };
		_btnOk.Click += OnOkClick;

		btnPanel.Controls.AddRange( btnCancel, _btnOk );

		Control content = mode switch
		{
			SetupMode.Hub => BuildHubContent(),
			SetupMode.Travel => BuildTravelContent(),
			_ => BuildResourceContent()
		};

		Controls.AddRange( content, _errorLabel, btnPanel );
		AcceptButton = _btnOk;
		CancelButton = btnCancel;

		Size = mode switch
		{
			SetupMode.Hub => new Size( 560, 450 ),
			SetupMode.Travel => new Size( 520, 220 ),
			_ => new Size( 460, 160 )
		};

		if ( mode == SetupMode.Hub ) _ = FetchPublicIpAsync();
	}

	private TableLayoutPanel BuildHubContent()
	{
		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 3,
			Padding = new Padding( 12, 12, 12, 4 ),
			AutoSize = false
		};
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );

		_tbResourceIp = AddRow( layout, "Resource Static IP:", _ctx.ResourceStaticIp, "e.g. 192.168.158.3" );

		// Hub public IP row with refresh button
		var lblPub = new Label { Text = "Hub Public IP:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding( 0, 8, 8, 2 ) };
		_tbHubPublicIp = new TextBox { Text = _ctx.HubPublicIp, Dock = DockStyle.Fill, Margin = new Padding( 0, 5, 4, 2 ), PlaceholderText = "Fetching..." };
		var btnRefresh = new Button { Text = "↺", Width = 28, Height = 23, Margin = new Padding( 0, 5, 0, 2 ), FlatStyle = FlatStyle.Flat };
		btnRefresh.Click += async ( _, _ ) => { btnRefresh.Enabled = false; await FetchPublicIpAsync(); btnRefresh.Enabled = true; };
		layout.Controls.AddRange( lblPub, _tbHubPublicIp, btnRefresh );

		// Conf directory row with Browse button
		var lblConf = new Label { Text = "Config Directory:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding( 0, 8, 8, 2 ) };
		_tbConfDir = new TextBox { Text = _ctx.ConfDirectory, Dock = DockStyle.Fill, Margin = new Padding( 0, 5, 4, 2 ) };
		var btnBrowse = new Button { Text = "...", Width = 28, Height = 23, Margin = new Padding( 0, 5, 0, 2 ), FlatStyle = FlatStyle.Flat };
		btnBrowse.Click += ( _, _ ) =>
		{
			using var dlg = new FolderBrowserDialog { SelectedPath = _tbConfDir.Text, Description = "Select directory for WireGuard .conf files" };
			if ( dlg.ShowDialog() == DialogResult.OK ) _tbConfDir.Text = dlg.SelectedPath;
		};
		layout.Controls.AddRange( lblConf, _tbConfDir, btnBrowse );

		// Travel machine names (multiline, spans all cols)
		var lblTravel = new Label { Text = "Travel Machine Names:", AutoSize = true, Margin = new Padding( 0, 10, 8, 2 ) };
		layout.Controls.Add( lblTravel );
		layout.SetColumnSpan( lblTravel, 3 );

		_tbTravelNames = new ()
		{
			Multiline = true,
			Height = 80,
			Dock = DockStyle.Fill,
			ScrollBars = ScrollBars.Vertical,
			Margin = new Padding( 0, 2, 0, 2 ),
			PlaceholderText = "One machine name per line (e.g. Laptop)\nLeave empty if no Travel machines yet.",
			Text = string.Join( Environment.NewLine, _ctx.TravelMachineNames )
		};
		layout.Controls.Add( _tbTravelNames );
		layout.SetColumnSpan( _tbTravelNames, 3 );

		// Skip WireGuard checkbox
		_cbSkipWg = new CheckBox
		{
			Text = "Skip WireGuard setup (LAN-only — no remote/Travel access needed)",
			Checked = _ctx.SkipWireGuard,
			AutoSize = true,
			Margin = new Padding( 0, 6, 0, 0 )
		};
		layout.Controls.Add( _cbSkipWg );
		layout.SetColumnSpan( _cbSkipWg, 3 );

		return layout;
	}

	private Panel BuildTravelContent()
	{
		var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding( 12, 10, 12, 4 ) };

		var note = new Label
		{
			Text = "Select the WireGuard .conf file generated on Hub for this machine.",
			Dock = DockStyle.Top,
			Height = 28,
			TextAlign = ContentAlignment.MiddleLeft
		};

		var inputRow = new Panel { Dock = DockStyle.Top, Height = 28 };

		var lblFile = new Label
		{
			Text = "Config File (.conf):",
			Dock = DockStyle.Left,
			Width = 122,
			TextAlign = ContentAlignment.MiddleLeft
		};
		var btnBrowse = new Button
		{
			Text = "...",
			Dock = DockStyle.Right,
			Width = 32,
			FlatStyle = FlatStyle.Flat
		};
		_tbConfFile = new TextBox
		{
			Dock = DockStyle.Fill,
			Text = _ctx.ConfFilePath,
			PlaceholderText = "e.g. C:\\BTR\\Extensibility\\ConduentResource\\Laptop-Tunnel.conf"
		};
		btnBrowse.Click += ( _, _ ) =>
		{
			using var dlg = new OpenFileDialog { Filter = "WireGuard Config|*.conf", Title = "Select WireGuard config file" };
			if ( _ctx.ConfDirectory.Length > 0 && Directory.Exists( _ctx.ConfDirectory ) ) dlg.InitialDirectory = _ctx.ConfDirectory;
			if ( dlg.ShowDialog() == DialogResult.OK ) _tbConfFile.Text = dlg.FileName;
		};

		// DockStyle.Left/Right/Fill order: Left first, Right second, Fill last
		inputRow.Controls.AddRange( lblFile, btnBrowse, _tbConfFile );

		// DockStyle.Top order: first added = topmost
		outer.Controls.AddRange( note, inputRow );

		return outer;
	}

	private static Label BuildResourceContent() =>
		new ()
		{
			Text = "Resource setup will install Python, pproxy, firewall rule, Windows Terminal profile, and startup shortcut.",
			Dock = DockStyle.Fill,
			TextAlign = ContentAlignment.MiddleLeft,
			Padding = new Padding( 12 )
		};

	private void OnOkClick( object? sender, EventArgs e )
	{
		var errors = ValidateInputs();
		if ( errors.Count > 0 )
		{
			_errorLabel.Text = string.Join( "\r\n", errors.Select( err => $"• {err}" ) );
			_errorLabel.Height = 18 + errors.Count * 18;
			_errorLabel.Visible = true;
			return;
		}
		ApplyToContext();
		DialogResult = DialogResult.OK;
		Close();
	}

	private List<string> ValidateInputs()
	{
		var errors = new List<string>();

		if ( _mode == SetupMode.Hub )
		{
			if ( string.IsNullOrWhiteSpace( _tbResourceIp!.Text ) ) errors.Add( "Resource Static IP is required" );
			if ( string.IsNullOrWhiteSpace( _tbHubPublicIp!.Text ) ) errors.Add( "Hub Public IP is required (needed for Travel tunnel Endpoint)" );
		}
		else if ( _mode == SetupMode.Travel )
		{
			var file = _tbConfFile?.Text.Trim() ?? "";
			if ( string.IsNullOrWhiteSpace( file ) ) { errors.Add( "Config file is required" ); return errors; }
			if ( !File.Exists( file ) ) { errors.Add( $"Config file not found: {file}" ); return errors; }
			var content = File.ReadAllText( file );
			if ( !content.Contains( "[Interface]" ) || !content.Contains( "PrivateKey" ) )
				errors.Add( "Config file does not appear to be a valid WireGuard configuration" );
		}
		return errors;
	}

	private void ApplyToContext()
	{
		if ( _mode == SetupMode.Hub )
		{
			_ctx.ResourceStaticIp = _tbResourceIp!.Text.Trim();
			_ctx.HubPublicIp = _tbHubPublicIp!.Text.Trim();
			_ctx.ConfDirectory = _tbConfDir!.Text.Trim();
			_ctx.SkipWireGuard = _cbSkipWg!.Checked;
			_ctx.TravelMachineNames = [.. _tbTravelNames!.Text
				.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(n => n.Length > 0)];
			var settings = AppSettings.Load();
			settings.ResourceStaticIp = _ctx.ResourceStaticIp;
			settings.PacDirectory = _ctx.ConfDirectory;
			settings.Save();
		}
		else if ( _mode == SetupMode.Travel )
		{
			_ctx.ConfFilePath = _tbConfFile!.Text.Trim();
			// Persist so subsequent setup runs pre-populate the path
			var settings = AppSettings.Load();
			settings.ConfFilePath = _ctx.ConfFilePath;
			settings.Save();
		}
	}

	private async Task FetchPublicIpAsync()
	{
		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromSeconds( 5 ) };
			var ip = ( await client.GetStringAsync( "https://api.ipify.org" ) ).Trim();
			if ( _tbHubPublicIp != null && !IsDisposed )
				Invoke( () => { if ( !IsDisposed ) _tbHubPublicIp.Text = ip; } );
		}
		catch { /* user enters manually */ }
	}

	private static TextBox AddRow( TableLayoutPanel layout, string label, string value, string placeholder = "" )
	{
		var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding( 0, 8, 8, 2 ) };
		var tb = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding( 0, 5, 0, 2 ), PlaceholderText = placeholder };
		layout.Controls.Add( lbl );
		layout.Controls.Add( tb );
		layout.Controls.Add( new Label() ); // empty 3rd col
		return tb;
	}
}