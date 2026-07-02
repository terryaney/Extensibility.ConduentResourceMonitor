namespace ConduentResourceMonitor.Setup;

public class SetupWizardForm : Form
{
	private const int MaxErrorEntries = 200;

	private readonly SetupMode _mode;
	private readonly SetupContext _ctx;
	private readonly bool _launchSupported;
	private readonly List<ISetupStep> _steps;
	private readonly StepStatus[] _statuses;
	private readonly bool[] _applicable;
	private readonly Dictionary<int, string> _lastFailureMessages = new();
	private readonly List<string> _errorHistory = new();
	private readonly List<(SetupInput Input, Func<string> Read)> _inputBindings = new();
	private readonly object _logSync = new();
	private string _logPath = "";
	private bool _logAvailable;
	private string? _logFailureMessage;
	private int _current;
	private bool _running;
	private bool _showingSummary;
	private bool _summaryExplicitlyReached;
	private bool _finishConfirmed;

	private Panel _stepPanel = null!;
	private Panel _summaryPanel = null!;
	private Panel _errorsPanel = null!;
	private Label _lblTitle = null!;
	private Label _lblProgress = null!;
	private Label _lblDesc = null!;
	private TableLayoutPanel _inputsPanel = null!;
	private Label _lblStatus = null!;
	private Label _lblSummaryLogHint = null!;
	private TableLayoutPanel _summaryTable = null!;
	private TextBox _txtErrors = null!;
	private CheckBox _chkLaunchApplication = null!;
	private Button _btnPrev = null!;
	private Button _btnNext = null!;
	private Button _btnSkip = null!;

	public bool LaunchApplication { get; private set; }

	// Hub LAN-only signal: the tunnel install was skipped rather than completed. Program.cs
	// persists this so runtime monitoring knows not to watch the WireGuard tunnel.
	public bool WireGuardSkipped =>
		_steps.Zip( _statuses, ( step, status ) => (step, status) )
			.Any( x => x.step is Steps.Hub.InstallHubTunnelStep && x.status != StepStatus.Complete );

	public SetupWizardForm( SetupMode mode, SetupContext ctx )
	{
		_mode = mode;
		_ctx = ctx;
		_launchSupported = true;
		_steps = StepFactory.Build( mode, ctx );
		_statuses = new StepStatus[ _steps.Count ];
		_applicable = new bool[ _steps.Count ];
		Array.Fill( _applicable, true );

		Text = $"Conduent Resource Setup — {mode}";
		Size = new Size( 860, 540 );
		MinimumSize = new Size( 760, 460 );
		StartPosition = FormStartPosition.CenterScreen;

		InitializeLog();
		BuildLayout();
		SelectStep( 0 );

		AcceptButton = _btnNext;
		FormClosing += OnFormClosing;

		_ = CheckApplicabilityAsync();
	}

	// Prefer the exe folder; fall back to LocalAppData when the install location is read-only.
	private static IEnumerable<string> LogPathCandidates()
	{
		yield return Path.Combine( AppContext.BaseDirectory, "setup.log" );

		var localAppData = Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData );
		if ( !string.IsNullOrWhiteSpace( localAppData ) )
			yield return Path.Combine( localAppData, "ConduentResourceMonitor", "setup.log" );
	}

	private void InitializeLog()
	{
		Exception? lastFailure = null;

		foreach ( var path in LogPathCandidates() )
		{
			try
			{
				var logDirectory = Path.GetDirectoryName( path );
				if ( !string.IsNullOrWhiteSpace( logDirectory ) )
					Directory.CreateDirectory( logDirectory );

				File.WriteAllText(
					path,
					$"Conduent setup log{Environment.NewLine}Mode: {_mode}{Environment.NewLine}Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{Environment.NewLine}" );
				_logPath = path;
				_logAvailable = true;
				_logFailureMessage = null;
				return;
			}
			catch ( Exception ex )
			{
				lastFailure = ex;
			}
		}

		RecordLogFailure( lastFailure ?? new IOException( "No writable log location found." ) );
	}

	private void AppendLog( string text, bool isError = false )
	{
		if ( isError )
			RecordError( text );

		if ( !_logAvailable ) return;

		try
		{
			lock ( _logSync )
				File.AppendAllText( _logPath, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}" );
		}
		catch ( Exception ex )
		{
			RecordLogFailure( ex );
		}
	}

	// Errors are kept in memory so they can still be shown when no log location is writable.
	private void RecordError( string text )
	{
		var entry = $"[{DateTime.Now:HH:mm:ss}] {text}";

		lock ( _logSync )
		{
			_errorHistory.Add( entry );
			if ( _errorHistory.Count > MaxErrorEntries )
				_errorHistory.RemoveRange( 0, _errorHistory.Count - MaxErrorEntries );
		}

		if ( IsDisposed || !IsHandleCreated ) return;

		if ( InvokeRequired )
			BeginInvoke( RefreshErrorsSurface );
		else
			RefreshErrorsSurface();
	}

	private void RecordLogFailure( Exception ex )
	{
		var shouldRecord = _logAvailable || _errorHistory.Count == 0 || !string.Equals( _logFailureMessage, ex.Message, StringComparison.Ordinal );
		_logAvailable = false;
		_logFailureMessage = ex.Message;
		if ( shouldRecord )
			RecordError( $"Logging unavailable: {ex.Message}" );

		if ( IsDisposed || !IsHandleCreated ) return;

		if ( InvokeRequired )
			BeginInvoke( UpdateLogHints );
		else
			UpdateLogHints();
	}

	private string BuildLogHintText() =>
		_logAvailable
			? $"Detailed setup output is written to {_logPath}."
			: $"setup.log is unavailable ({_logFailureMessage ?? "logging could not be initialized"}). Errors are shown below.";

	private void UpdateLogHints()
	{
		if ( _lblSummaryLogHint is not null ) _lblSummaryLogHint.Text = BuildLogHintText();
		if ( _errorsPanel is not null ) _errorsPanel.Visible = !_logAvailable;
		RefreshErrorsSurface();
	}

	private string BuildStepMessage( string message )
	{
		if ( !message.Contains( "setup.log", StringComparison.OrdinalIgnoreCase ) ) return message;
		if ( _logAvailable ) return message.Replace( "setup.log", _logPath, StringComparison.OrdinalIgnoreCase );

		return message
			.Replace( "Check setup.log.", "Errors are shown below.", StringComparison.OrdinalIgnoreCase )
			.Replace( "See setup.log.", "Errors are shown below.", StringComparison.OrdinalIgnoreCase )
			.Replace( "Review setup.log.", "Errors are shown below.", StringComparison.OrdinalIgnoreCase )
			.Replace( "setup.log", "the errors shown below", StringComparison.OrdinalIgnoreCase );
	}

	private void RefreshErrorsSurface()
	{
		if ( _txtErrors is null ) return;

		string text;
		lock ( _logSync )
			text = _errorHistory.Count == 0
				? "No errors recorded yet."
				: string.Join( Environment.NewLine, _errorHistory );

		_txtErrors.Text = text;
		_txtErrors.SelectionStart = _txtErrors.TextLength;
		_txtErrors.ScrollToCaret();
	}

	private void BuildLayout()
	{
		var main = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 1,
			RowCount = 2
		};
		main.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		main.RowStyles.Add( new RowStyle( SizeType.Absolute, 52 ) );

		var contentHost = new Panel
		{
			Dock = DockStyle.Fill,
			Padding = new Padding( 16, 12, 16, 8 )
		};

		_stepPanel = BuildStepPanel();
		_summaryPanel = BuildSummaryPanel();
		_summaryPanel.Visible = false;
		_errorsPanel = BuildErrorsPanel();

		contentHost.Controls.Add( _summaryPanel );
		contentHost.Controls.Add( _stepPanel );
		contentHost.Controls.Add( _errorsPanel );

		main.Controls.Add( contentHost, 0, 0 );
		main.Controls.Add( BuildNavBar(), 0, 1 );

		Controls.Add( main );
	}

	private Panel BuildStepPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

		var header = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			ColumnCount = 2,
			Height = 40
		};
		header.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		header.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );

		_lblTitle = new Label
		{
			Dock = DockStyle.Fill,
			Font = new Font( "Segoe UI", 13f, FontStyle.Bold ),
			TextAlign = ContentAlignment.MiddleLeft
		};

		_lblProgress = new Label
		{
			AutoSize = true,
			Anchor = AnchorStyles.Right,
			Font = new Font( "Segoe UI", 9f ),
			ForeColor = Color.FromArgb( 80, 80, 80 ),
			TextAlign = ContentAlignment.MiddleRight
		};

		header.Controls.Add( _lblTitle, 0, 0 );
		header.Controls.Add( _lblProgress, 1, 0 );

		_lblDesc = new Label
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			Font = new Font( "Segoe UI", 9.5f ),
			Padding = new Padding( 0, 0, 0, 8 )
		};

		_inputsPanel = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 3,
			Padding = new Padding( 0, 0, 0, 8 )
		};
		_inputsPanel.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		_inputsPanel.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		_inputsPanel.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );

		_lblStatus = new Label
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			Font = new Font( "Segoe UI", 9.5f ),
			Padding = new Padding( 0, 4, 0, 4 ),
			Visible = false
		};

		// Dock.Top siblings render in reverse add order — last added claims the top-most slice.
		panel.Controls.Add( _lblStatus );
		panel.Controls.Add( _inputsPanel );
		panel.Controls.Add( _lblDesc );
		panel.Controls.Add( header );

		return panel;
	}

	private void BuildInputRows( ISetupStep step )
	{
		_inputBindings.Clear();
		_inputsPanel.SuspendLayout();
		_inputsPanel.Controls.Clear();
		_inputsPanel.RowStyles.Clear();
		_inputsPanel.RowCount = 0;

		var inputs = step.Inputs;
		_inputsPanel.Visible = inputs.Count > 0;

		for ( var i = 0; i < inputs.Count; i++ )
		{
			var input = inputs[ i ];
			_inputsPanel.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
			_inputsPanel.RowCount++;

			var lbl = new Label
			{
				Text = $"{input.Label}:",
				AutoSize = true,
				Anchor = input.Kind == SetupInputKind.MultilineText ? AnchorStyles.Left | AnchorStyles.Top : AnchorStyles.Left,
				Margin = new Padding( 0, 8, 8, 2 )
			};

			var tb = new TextBox
			{
				Text = input.Get(),
				Dock = DockStyle.Fill,
				Margin = new Padding( 0, 5, 4, 2 ),
				PlaceholderText = input.Placeholder
			};
			if ( input.Kind == SetupInputKind.MultilineText )
			{
				tb.Multiline = true;
				tb.Height = 72;
				tb.ScrollBars = ScrollBars.Vertical;
				tb.AcceptsReturn = true;
			}

			var action = BuildInputActionButton( input, tb );

			_inputsPanel.Controls.Add( lbl, 0, i );
			_inputsPanel.Controls.Add( tb, 1, i );
			if ( action != null ) _inputsPanel.Controls.Add( action, 2, i );

			_inputBindings.Add( (input, () => tb.Text.Trim()) );

			if ( input.AutoFetch != null && tb.Text.Length == 0 )
				_ = AutoFetchAsync( input, tb );
		}

		_inputsPanel.ResumeLayout();
	}

	private Button? BuildInputActionButton( SetupInput input, TextBox tb )
	{
		switch ( input.Kind )
		{
			case SetupInputKind.FilePath:
			{
				var btn = MakeInputButton( "..." );
				btn.Click += ( _, _ ) =>
				{
					using var dlg = new OpenFileDialog { Filter = input.FileFilter, Title = $"Select {input.Label}" };
					var dir = Path.GetDirectoryName( tb.Text.Trim() );
					if ( !string.IsNullOrEmpty( dir ) && Directory.Exists( dir ) ) dlg.InitialDirectory = dir;
					if ( dlg.ShowDialog( this ) == DialogResult.OK ) tb.Text = dlg.FileName;
				};
				return btn;
			}
			case SetupInputKind.FolderPath:
			{
				var btn = MakeInputButton( "..." );
				btn.Click += ( _, _ ) =>
				{
					using var dlg = new FolderBrowserDialog { SelectedPath = tb.Text.Trim(), Description = $"Select {input.Label}" };
					if ( dlg.ShowDialog( this ) == DialogResult.OK ) tb.Text = dlg.SelectedPath;
				};
				return btn;
			}
			default:
			{
				if ( input.AutoFetch == null ) return null;
				var btn = MakeInputButton( "↺" );
				btn.Click += async ( _, _ ) =>
				{
					btn.Enabled = false;
					tb.Text = string.Empty;
					await AutoFetchAsync( input, tb );
					btn.Enabled = true;
				};
				return btn;
			}
		}
	}

	private static Button MakeInputButton( string text )
	{
		var btn = new Button { Text = text, Width = 28, Height = 23, Margin = new Padding( 0, 5, 0, 2 ), FlatStyle = FlatStyle.Flat };
		btn.FlatAppearance.BorderColor = Color.FromArgb( 176, 180, 190 );
		return btn;
	}

	private async Task AutoFetchAsync( SetupInput input, TextBox tb )
	{
		try
		{
			var value = await input.AutoFetch!();
			if ( !IsDisposed && !tb.IsDisposed && tb.Text.Length == 0 )
				tb.Text = value;
		}
		catch
		{
			// User enters the value manually.
		}
	}

	// Validates (when requested) and pushes the current step's input values into the context,
	// persisting them for future setup runs. Returns false when validation blocked the action.
	private bool TryApplyInputs( bool validate )
	{
		if ( _inputBindings.Count == 0 ) return true;

		if ( validate )
		{
			var errors = _inputBindings
				.Select( b => b.Input.Validate?.Invoke( b.Read() ) )
				.Where( e => !string.IsNullOrEmpty( e ) )
				.ToList();
			if ( errors.Count > 0 )
			{
				SetStatusText( string.Join( "\r\n", errors.Select( e => $"• {e}" ) ), Color.Red );
				return false;
			}
		}

		foreach ( var (input, read) in _inputBindings )
			input.Set( read() );
		_ctx.PersistInputs( _mode );
		return true;
	}

	private Panel BuildSummaryPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill };

		var title = new Label
		{
			Dock = DockStyle.Top,
			Height = 38,
			Font = new Font( "Segoe UI", 13f, FontStyle.Bold ),
			Text = "Setup complete"
		};

		var desc = new Label
		{
			Dock = DockStyle.Top,
			Height = 44,
			Font = new Font( "Segoe UI", 9.5f ),
			Text = "Review the final state for each step before closing the wizard."
		};

		_chkLaunchApplication = new CheckBox
		{
			Dock = DockStyle.Top,
			Height = 28,
			Checked = _launchSupported,
			Enabled = _launchSupported,
			Font = new Font( "Segoe UI", 9.5f ),
			Text = "Launch application"
		};

		_lblSummaryLogHint = new Label
		{
			Dock = DockStyle.Bottom,
			AutoSize = true,
			Font = new Font( "Segoe UI", 8.5f ),
			ForeColor = Color.FromArgb( 96, 96, 96 ),
			Padding = new Padding( 0, 8, 0, 0 ),
			Text = BuildLogHintText()
		};

		var listHost = new Panel
		{
			Dock = DockStyle.Fill,
			AutoScroll = true,
			Padding = new Padding( 0, 8, 0, 0 )
		};

		_summaryTable = new TableLayoutPanel
		{
			Dock = DockStyle.Top,
			AutoSize = true,
			ColumnCount = 2
		};
		_summaryTable.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		_summaryTable.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		listHost.Controls.Add( _summaryTable );

		panel.Controls.Add( listHost );
		panel.Controls.Add( _lblSummaryLogHint );
		panel.Controls.Add( _chkLaunchApplication );
		panel.Controls.Add( desc );
		panel.Controls.Add( title );

		return panel;
	}

	// Only shown when no log location is writable — errors then have nowhere else to go.
	private Panel BuildErrorsPanel()
	{
		var panel = new Panel
		{
			Dock = DockStyle.Bottom,
			Height = 152,
			Padding = new Padding( 0, 12, 0, 0 ),
			Visible = !_logAvailable
		};

		var title = new Label
		{
			Dock = DockStyle.Top,
			Height = 22,
			Font = new Font( "Segoe UI", 9f, FontStyle.Bold ),
			Text = "Errors"
		};

		_txtErrors = new TextBox
		{
			BackColor = Color.White,
			BorderStyle = BorderStyle.FixedSingle,
			Dock = DockStyle.Fill,
			Font = new Font( "Consolas", 8.75f ),
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Vertical,
			WordWrap = false
		};

		panel.Controls.Add( _txtErrors );
		panel.Controls.Add( title );

		RefreshErrorsSurface();
		return panel;
	}

	private Control BuildNavBar()
	{
		var bar = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			Padding = new Padding( 12, 8, 12, 8 ),
			BackColor = Color.FromArgb( 240, 241, 245 )
		};
		bar.ColumnStyles.Add( new ColumnStyle( SizeType.AutoSize ) );
		bar.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );

		_btnPrev = MakeNavButton( "Previous" );
		_btnPrev.Margin = new Padding( 0 );
		_btnPrev.Click += ( _, _ ) => Navigate( -1 );

		var right = new FlowLayoutPanel
		{
			AutoSize = true,
			FlowDirection = FlowDirection.LeftToRight,
			Anchor = AnchorStyles.Right,
			Margin = new Padding( 0 )
		};

		_btnSkip = MakeNavButton( "Skip" );
		_btnSkip.Click += ( _, _ ) => OnSkipStep();

		_btnNext = MakeNavButton( "Next" );
		_btnNext.Click += async ( _, _ ) => await OnNextAsync();

		right.Controls.Add( _btnSkip );
		right.Controls.Add( _btnNext );

		bar.Controls.Add( _btnPrev, 0, 0 );
		bar.Controls.Add( right, 1, 0 );

		return bar;
	}

	private static Button MakeNavButton( string text )
	{
		var btn = new Button
		{
			Text = text,
			AutoSize = true,
			MinimumSize = new Size( 88, 30 ),
			FlatStyle = FlatStyle.Flat,
			BackColor = Color.White,
			ForeColor = Color.FromArgb( 32, 32, 32 ),
			Margin = new Padding( 8, 0, 0, 0 )
		};
		btn.FlatAppearance.BorderColor = Color.FromArgb( 176, 180, 190 );
		btn.FlatAppearance.MouseOverBackColor = Color.FromArgb( 244, 246, 250 );
		return btn;
	}

	private void SelectStep( int index )
	{
		_showingSummary = false;
		_stepPanel.Visible = true;
		_summaryPanel.Visible = false;
		_current = index;

		var step = _steps[ index ];
		_lblTitle.Text = step.Title;
		_lblProgress.Text = $"Step {index + 1} of {_steps.Count}";
		_lblDesc.Text = step.Description;
		BuildInputRows( step );

		if ( !_applicable[ index ] )
			SetStatusText( "This step is not applicable on this machine and will be skipped.", Color.Gray );
		else if ( _statuses[ index ] == StepStatus.Failed && _lastFailureMessages.TryGetValue( index, out var message ) && !string.IsNullOrWhiteSpace( message ) )
			SetStatusText( BuildStepMessage( message ), Color.Red );
		else
			SetStatusText( string.Empty, Color.Gray );

		UpdateNavigation();
	}

	private void SetStatusText( string text, Color color )
	{
		_lblStatus.Text = text;
		_lblStatus.ForeColor = color;
		_lblStatus.Visible = text.Length > 0;
	}

	private bool CanFinish() =>
		_statuses.Zip(
			_applicable,
			( status, applicable ) => !applicable || status == StepStatus.Complete || status == StepStatus.Skipped )
		.All( x => x );

	private void UpdateNavigation()
	{
		if ( _showingSummary )
		{
			_btnPrev.Visible = false;
			_btnSkip.Visible = false;
			_btnNext.Text = "Finish";
			_btnNext.Enabled = !_running;
			return;
		}

		_btnPrev.Visible = true;
		_btnPrev.Enabled = !_running && _current > 0;
		_btnSkip.Visible = true;
		_btnSkip.Enabled = !_running;
		_btnNext.Text = "Next";
		_btnNext.Enabled = !_running;
	}

	private void Navigate( int delta )
	{
		var next = _current + delta;
		if ( next >= 0 && next < _steps.Count )
			SelectStep( next );
	}

	private async Task OnNextAsync()
	{
		if ( _running ) return;

		if ( _showingSummary )
		{
			_finishConfirmed = true;
			LaunchApplication = _launchSupported && _chkLaunchApplication.Checked;
			DialogResult = DialogResult.OK;
			Close();
			return;
		}

		var step = _steps[ _current ];

		if ( !_applicable[ _current ] )
		{
			AdvanceAfterResolution();
			return;
		}

		if ( !TryApplyInputs( validate: true ) ) return;

		if ( step.IsManual )
		{
			AppendLog( $"MANUAL DONE [{_current + 1}/{_steps.Count}] {step.Title}" );
			SetStepStatus( _current, StepStatus.Complete );
			AdvanceAfterResolution();
			return;
		}

		await OnRunStepAsync();
	}

	private void AdvanceAfterResolution()
	{
		if ( _current < _steps.Count - 1 )
		{
			SelectStep( _current + 1 );
			return;
		}

		if ( CanFinish() )
		{
			ShowSummary();
			return;
		}

		SelectStep( _current );
		SetStatusText( "Some steps are still pending or failed. Use Previous to revisit them, or Skip to bypass them.", Color.Red );
	}

	private async Task OnRunStepAsync()
	{
		if ( _running ) return;

		var step = _steps[ _current ];
		_running = true;
		SetStepStatus( _current, StepStatus.Running );
		SetStatusText( string.Empty, Color.Gray );
		UseWaitCursor = true;
		UpdateNavigation();
		AppendLog( $"START [{_current + 1}/{_steps.Count}] {step.Title}" );

		// Step progress goes to setup.log only — on screen the disabled buttons and wait
		// cursor indicate the step is running.
		var progress = new Progress<string>( msg => AppendLog( msg ) );

		SetupStepResult result;
		try
		{
			// Steps that are unsafe or wasteful to repeat (reinstalls, key regeneration,
			// duplicate rules) verify completion instead of re-running.
			if ( !step.RerunWhenComplete && await Task.Run( step.IsAlreadyCompleteAsync ) )
				result = new SetupStepResult( true, "Already complete — nothing to do." );
			else
				result = await Task.Run( () => step.RunAsync( progress ) );
		}
		catch ( Exception ex )
		{
			result = new SetupStepResult( false, ex.Message );
		}

		AppendLog( $"{( result.Ok ? "DONE" : "FAIL" )} [{_current + 1}/{_steps.Count}] {step.Title}: {result.Message}", isError: !result.Ok );
		SetStepStatus( _current, result.Ok ? StepStatus.Complete : StepStatus.Failed );
		if ( result.Ok )
			_lastFailureMessages.Remove( _current );
		else
			_lastFailureMessages[ _current ] = result.Message;

		_running = false;
		UseWaitCursor = false;
		if ( result.Ok )
			AdvanceAfterResolution();
		else
			SelectStep( _current );
	}

	private void OnSkipStep()
	{
		if ( _running ) return;

		// Keep any values the user typed even when skipping — later steps share them.
		TryApplyInputs( validate: false );
		AppendLog( $"SKIP [{_current + 1}/{_steps.Count}] {_steps[ _current ].Title}" );
		SetStepStatus( _current, StepStatus.Skipped );
		AdvanceAfterResolution();
	}

	private void SetStepStatus( int index, StepStatus status ) => _statuses[ index ] = status;

	private void ShowSummary()
	{
		if ( !CanFinish() ) return;

		_showingSummary = true;
		_summaryExplicitlyReached = true;
		RefreshSummary();
		_stepPanel.Visible = false;
		_summaryPanel.Visible = true;
		UpdateNavigation();
	}

	private void RefreshSummary()
	{
		_summaryTable.SuspendLayout();
		_summaryTable.Controls.Clear();
		_summaryTable.RowStyles.Clear();
		_summaryTable.RowCount = 0;

		for ( var i = 0; i < _steps.Count; i++ )
		{
			_summaryTable.RowStyles.Add( new RowStyle( SizeType.AutoSize ) );
			_summaryTable.RowCount++;

			var title = new Label
			{
				AutoSize = true,
				Margin = new Padding( 0, 0, 12, 10 ),
				Font = new Font( "Segoe UI", 9.5f ),
				Text = _steps[ i ].Title
			};

			var state = new Label
			{
				AutoSize = true,
				Margin = new Padding( 0, 0, 0, 10 ),
				Font = new Font( "Segoe UI", 9.5f, FontStyle.Bold ),
				ForeColor = _statuses[ i ] == StepStatus.Complete ? Color.FromArgb( 0, 128, 64 ) : Color.FromArgb( 120, 120, 120 ),
				Text = _statuses[ i ] == StepStatus.Complete ? "Done" : "Skipped"
			};

			_summaryTable.Controls.Add( title, 0, i );
			_summaryTable.Controls.Add( state, 1, i );
		}

		_summaryTable.ResumeLayout();
	}

	private async Task CheckApplicabilityAsync()
	{
		for ( var i = 0; i < _steps.Count; i++ )
		{
			var idx = i;
			var step = _steps[ idx ];

			var applicable = await step.IsApplicableAsync();
			_applicable[ idx ] = applicable;

			if ( !applicable )
			{
				SetStepStatus( idx, StepStatus.Skipped );
				AppendLog( $"AUTO-SKIP [{idx + 1}/{_steps.Count}] {step.Title}: not applicable" );
			}
		}

		if ( IsDisposed ) return;

		if ( IsHandleCreated )
			BeginInvoke( () => SelectStep( _current ) );
		else
			SelectStep( _current );
	}

	private void OnFormClosing( object? sender, FormClosingEventArgs e )
	{
		if ( _running )
		{
			e.Cancel = true;
			return;
		}

		if ( CanFinish() )
		{
			LaunchApplication = _finishConfirmed &&
				_summaryExplicitlyReached &&
				_launchSupported &&
				_chkLaunchApplication.Checked;
			DialogResult = DialogResult.OK;
		}
	}
}
