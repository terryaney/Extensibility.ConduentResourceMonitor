using System.Text.RegularExpressions;

namespace ConduentResourceMonitor.Setup;

public class AddTravelConfigForm : Form
{
	private readonly TextBox _tbName;
	private readonly TextBox _tbConfDir;
	private readonly TextBox _tbIp;
	private readonly RichTextBox _outputBox;
	private readonly Button _btnGenerate;
	private bool _running;

	public AddTravelConfigForm( Options options )
	{
		Text = "Add Travel Machine Config";
		Size = new Size( 620, 480 );
		StartPosition = FormStartPosition.CenterScreen;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;

		var confDir = options.ConfDirectory ?? @"C:\BTR\Extensibility\ConduentResource";

		var layout = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			ColumnCount = 3,
			Height = 148,
			Padding = new Padding( 12, 12, 12, 4 )
		};
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		layout.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );

		_tbName = AddRow( layout, "Machine Name:", "", "e.g. Laptop or WorkPC" );
		_tbConfDir = AddRow( layout, "Config Directory:", confDir, "" );

		var btnBrowse = new Button { Text = "...", Width = 28, Height = 23, FlatStyle = FlatStyle.Flat };
		btnBrowse.Click += ( _, _ ) =>
		{
			using var dlg = new FolderBrowserDialog { SelectedPath = _tbConfDir.Text };
			if ( dlg.ShowDialog() == DialogResult.OK ) _tbConfDir.Text = dlg.SelectedPath;
		};
		// Replace the empty cell in the third column of the last row with Browse button
		layout.Controls.Remove( layout.Controls[ layout.Controls.Count - 1 ] );
		layout.Controls.Add( btnBrowse );

		var suggestedIp = SuggestNextIp( confDir );
		_tbIp = AddRow( layout, "WireGuard IP:", suggestedIp, "e.g. 10.0.0.3" );

		_outputBox = new RichTextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			BackColor = Color.FromArgb( 250, 250, 255 ),
			Font = new Font( "Consolas", 9f ),
			BorderStyle = BorderStyle.FixedSingle,
			ScrollBars = RichTextBoxScrollBars.Vertical
		};

		var btnPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			Height = 44,
			FlowDirection = FlowDirection.RightToLeft,
			Padding = new Padding( 8, 6, 8, 6 )
		};
		var btnClose = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
		_btnGenerate = new Button { Text = "Generate Config", AutoSize = true };
		_btnGenerate.Click += async ( _, _ ) => await OnGenerateAsync();
		btnPanel.Controls.AddRange( btnClose, _btnGenerate );

		Controls.AddRange( _outputBox, layout, btnPanel );
		CancelButton = btnClose;
	}

	private async Task OnGenerateAsync()
	{
		if ( _running ) return;

		var name = _tbName.Text.Trim();
		var confDir = _tbConfDir.Text.Trim();
		var ip = _tbIp.Text.Trim();

		var errors = new List<string>();
		if ( string.IsNullOrEmpty( name ) ) errors.Add( "Machine name is required" );
		if ( string.IsNullOrEmpty( confDir ) ) errors.Add( "Config directory is required" );
		if ( string.IsNullOrEmpty( ip ) ) errors.Add( "WireGuard IP is required" );

		var hubConf = Path.Combine( confDir, "Hub-Tunnel.conf" );
		if ( !File.Exists( hubConf ) ) errors.Add( $"Hub-Tunnel.conf not found in: {confDir}" );

		if ( errors.Count > 0 )
		{
			AppendOutput( string.Join( "\r\n", errors.Select( e => "• " + e ) ) + "\r\n", Color.Red );
			return;
		}

		_running = true;
		_btnGenerate.Enabled = false;
		_outputBox.Clear();

		try
		{
			AppendOutput( $"Generating key pair for {name}...\r\n", Color.FromArgb( 0, 102, 204 ) );
			var privKey = await RunWgCommand( "genkey" );
			var pubKey = await RunWgCommand( "pubkey", privKey );
			AppendOutput( $"Public key: {pubKey}\r\n" );

			// Read hub conf to get Hub public key and update it
			var hubConfText = File.ReadAllText( hubConf );
			var hubPubKey = ExtractHubPublicKey( hubConfText );
			var hubEndpoint = ExtractHubEndpoint( hubConfText );

			// Append peer to Hub-Tunnel.conf
			var peerBlock = $"\r\n[Peer]\r\n# {name}\r\nPublicKey = {pubKey}\r\nAllowedIPs = {ip}/32\r\n";
			File.AppendAllText( hubConf, peerBlock );
			AppendOutput( $"Added peer to Hub-Tunnel.conf\r\n" );

			// Write Travel conf
			var travelConf = $"""
                [Interface]
                PrivateKey = {privKey}
                Address = {ip}/32

                [Peer]
                PublicKey = {hubPubKey}
                AllowedIPs = 10.0.0.1/32
                {( hubEndpoint.Length > 0 ? $"Endpoint = {hubEndpoint}" : "# Endpoint = <hub-public-ip>:51820" )}
                PersistentKeepalive = 25
                """;
			var travelConfPath = Path.Combine( confDir, $"{name}-Tunnel.conf" );
			File.WriteAllText( travelConfPath, travelConf );
			AppendOutput( $"Written: {travelConfPath}\r\n" );

			// Reinstall Hub tunnel service
			AppendOutput( "Reinstalling Hub tunnel service (requires UAC)...\r\n" );
			var commands = new List<ElevatedCommand>
			{
				new()
				{
					FileName = ProcessHelper.WireGuardExePath,
					Arguments = ["/uninstalltunnelservice", "Hub-Tunnel"],
					SuccessExitCodes = [0, 1],
					Description = "Uninstalling existing Hub-Tunnel service"
				},
				new()
				{
					FileName = "powershell.exe",
					Arguments = ["-NoProfile", "-Command", "Start-Sleep -Seconds 3"],
					Description = "Waiting before reinstalling Hub-Tunnel"
				},
				new()
				{
					FileName = ProcessHelper.WireGuardExePath,
					Arguments = ["/installtunnelservice", hubConf],
					Description = "Installing updated Hub-Tunnel service"
				}
			};
			await ProcessHelper.RunElevatedCommandsAsync( commands );

			AppendOutput( $"\r\n✓ Done! Copy {travelConfPath} to {name} and run Travel setup.\r\n", Color.FromArgb( 0, 128, 64 ) );
			AppendOutput( $"\r\nTravel setup command:\r\nConduentResourceMonitor.exe --setup Travel --conf-file \"{travelConfPath}\"\r\n" );

			// Suggest next IP for future machines
			_tbIp.Text = SuggestNextIp( confDir );
			_tbName.Clear();
			_tbName.Focus();
		}
		catch ( Exception ex )
		{
			AppendOutput( $"\r\n✗ Error: {ex.Message}\r\n", Color.Red );
		}
		finally
		{
			_running = false;
			_btnGenerate.Enabled = true;
		}
	}

	private static async Task<string> RunWgCommand( string args, string? stdin = null )
	{
		var ( exitCode, output ) = stdin == null
			? await ProcessHelper.RunAsync( "wg", args )
			: await ProcessHelper.RunWithInputAsync( "wg", args, stdin );

		return ProcessHelper.ValidateWireGuardKeyOutput( $"wg {args}", exitCode, output, "WireGuard" );
	}

	private static string ExtractHubPublicKey( string hubConfText )
	{
		// Look for PublicKey in the first [Peer] block
		var m = Regex.Match( hubConfText, @"\[Peer\].*?PublicKey\s*=\s*(.+)", RegexOptions.Singleline );
		return m.Success ? m.Groups[ 1 ].Value.Split( '\n' )[ 0 ].Trim() : "<hub-public-key>";
	}

	private static string ExtractHubEndpoint( string hubConfText )
	{
		// Look for Endpoint in any [Peer] block
		var m = Regex.Match( hubConfText, @"Endpoint\s*=\s*(.+)" );
		return m.Success ? m.Groups[ 1 ].Value.Trim() : "";
	}

	private static string SuggestNextIp( string confDir )
	{
		if ( !Directory.Exists( confDir ) ) return "10.0.0.2";

		var usedIps = new HashSet<int>();

		foreach ( var conf in Directory.GetFiles( confDir, "*.conf" ) )
		{
			if ( Path.GetFileName( conf ).Equals( "Hub-Tunnel.conf", StringComparison.OrdinalIgnoreCase ) ) continue;
			var text = File.ReadAllText( conf );
			var m = Regex.Match( text, @"Address\s*=\s*10\.0\.0\.(\d+)" );
			if ( m.Success && int.TryParse( m.Groups[ 1 ].Value, out var octet ) ) usedIps.Add( octet );
		}
		for ( var i = 2; i < 255; i++ )
			if ( !usedIps.Contains( i ) ) return $"10.0.0.{i}";

		return "10.0.0.2";
	}

	private void AppendOutput( string text, Color? color = null )
	{
		_outputBox.SelectionStart = _outputBox.TextLength;
		_outputBox.SelectionLength = 0;
		_outputBox.SelectionColor = color ?? _outputBox.ForeColor;
		_outputBox.AppendText( text );
		_outputBox.ScrollToCaret();
	}

	private static TextBox AddRow( TableLayoutPanel layout, string label, string value, string placeholder )
	{
		var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding( 0, 8, 8, 2 ) };
		var tb = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new Padding( 0, 5, 0, 2 ), PlaceholderText = placeholder };
		layout.Controls.Add( lbl );
		layout.Controls.Add( tb );
		layout.Controls.Add( new Label() ); // empty 3rd col
		return tb;
	}
}