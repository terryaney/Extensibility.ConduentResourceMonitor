namespace ConduentResourceMonitor.Setup;

public class SetupWizardForm : Form
{
	private readonly SetupMode _mode;
	private readonly SetupContext _ctx;
	private readonly List<ISetupStep> _steps;
	private readonly StepStatus[] _statuses;
	private readonly bool[] _applicable;
	private int _current;
	private bool _running;

	private ListBox _stepList = null!;
	private Label _lblTitle = null!;
	private Label _lblBadges = null!;
	private Label _lblDesc = null!;
	private RichTextBox _outputBox = null!;
	private Button _btnRun = null!;
	private Button _btnMarkDone = null!;
	private Button _btnSkip = null!;
	private Button _btnPrev = null!;
	private Button _btnNext = null!;
	private Button _btnFinish = null!;
	private Label _lblProgress = null!;

	public SetupWizardForm( SetupMode mode, SetupContext ctx )
	{
		_mode = mode;
		_ctx = ctx;
		_steps = StepFactory.Build( mode, ctx );
		_statuses = new StepStatus[ _steps.Count ];
		_applicable = new bool[ _steps.Count ];
		Array.Fill( _applicable, true );

		Text = $"Conduent Resource Setup — {mode}";
		Size = new Size( 960, 660 );
		MinimumSize = new Size( 820, 560 );
		StartPosition = FormStartPosition.CenterScreen;

		BuildLayout();
		SelectStep( 0 );
		
		_ = CheckInitialStatusesAsync();
	}

	private void BuildLayout()
	{
		var main = new TableLayoutPanel
		{
			Dock = DockStyle.Fill,
			ColumnCount = 2,
			RowCount = 2
		};
		main.ColumnStyles.Add( new ColumnStyle( SizeType.Absolute, 260 ) );
		main.ColumnStyles.Add( new ColumnStyle( SizeType.Percent, 100 ) );
		main.RowStyles.Add( new RowStyle( SizeType.Percent, 100 ) );
		main.RowStyles.Add( new RowStyle( SizeType.Absolute, 50 ) );

		main.Controls.Add( BuildLeftPanel(), 0, 0 );
		main.Controls.Add( BuildRightPanel(), 1, 0 );

		var nav = BuildNavBar();
		main.Controls.Add( nav, 0, 1 );
		main.SetColumnSpan( nav, 2 );

		Controls.Add( main );
	}

	private Panel BuildLeftPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb( 242, 242, 248 ) };

		var header = new Label
		{
			Text = $"  {_mode.ToString().ToUpper()} SETUP",
			Font = new Font( "Segoe UI", 8.5f, FontStyle.Bold ),
			ForeColor = Color.FromArgb( 96, 96, 112 ),
			Dock = DockStyle.Top,
			Height = 32,
			TextAlign = ContentAlignment.MiddleLeft,
			BackColor = Color.FromArgb( 232, 232, 242 )
		};

		_stepList = new ListBox
		{
			Dock = DockStyle.Fill,
			BorderStyle = BorderStyle.None,
			Font = new Font( "Segoe UI", 9.5f ),
			BackColor = Color.FromArgb( 242, 242, 248 ),
			DrawMode = DrawMode.OwnerDrawFixed,
			ItemHeight = 32
		};
		_stepList.DrawItem += OnDrawStepItem;
		_stepList.SelectedIndexChanged += ( _, _ ) =>
		{
			if ( !_running && _stepList.SelectedIndex >= 0 && _stepList.SelectedIndex != _current )
				SelectStep( _stepList.SelectedIndex );
		};

		foreach ( var s in _steps )
			_stepList.Items.Add( s.Title );

		panel.Controls.AddRange( _stepList, header );

		return panel;
	}

	private void OnDrawStepItem( object? sender, DrawItemEventArgs e )
	{
		if ( e.Index < 0 || e.Index >= _steps.Count ) return;

		var status = _statuses[ e.Index ];
		var isSelected = ( e.State & DrawItemState.Selected ) == DrawItemState.Selected;

		var bgColor = isSelected ? Color.FromArgb( 0, 102, 204 ) : Color.FromArgb( 242, 242, 248 );
		e.Graphics.FillRectangle( new SolidBrush( bgColor ), e.Bounds );

		var fgColor = isSelected ? Color.White : status switch
		{
			StepStatus.Complete => Color.FromArgb( 0, 128, 64 ),
			StepStatus.Failed => Color.FromArgb( 180, 0, 0 ),
			StepStatus.Skipped => Color.FromArgb( 128, 128, 128 ),
			StepStatus.Running => Color.FromArgb( 0, 102, 204 ),
			_ => _applicable[ e.Index ] ? Color.FromArgb( 30, 30, 30 ) : Color.FromArgb( 160, 160, 160 )
		};

		var icon = status switch
		{
			StepStatus.Complete => "✓",
			StepStatus.Failed => "✗",
			StepStatus.Skipped => "⊘",
			StepStatus.Running => "▶",
			_ => "○"
		};

		var bounds = new Rectangle( e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height );
		TextRenderer.DrawText( e.Graphics, $"{icon}  {_steps[ e.Index ].Title}", e.Font ?? _stepList.Font,
			bounds, fgColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis );
	}

	private Panel BuildRightPanel()
	{
		var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding( 16, 12, 16, 8 ) };

		_lblTitle = new Label
		{
			Font = new Font( "Segoe UI", 13f, FontStyle.Bold ),
			AutoSize = false,
			Height = 32,
			Dock = DockStyle.Top,
			Text = ""
		};

		_lblBadges = new Label
		{
			Font = new Font( "Segoe UI", 8.5f ),
			ForeColor = Color.FromArgb( 80, 80, 140 ),
			AutoSize = false,
			Height = 20,
			Dock = DockStyle.Top,
			Text = ""
		};

		_lblDesc = new Label
		{
			Font = new Font( "Segoe UI", 9.5f ),
			AutoSize = false,
			Height = 90,
			Dock = DockStyle.Top,
			Text = ""
		};

		var separator = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb( 200, 200, 220 ), Margin = new Padding( 0, 4, 0, 4 ) };

		_outputBox = new RichTextBox
		{
			Dock = DockStyle.Fill,
			ReadOnly = true,
			BackColor = Color.FromArgb( 250, 250, 255 ),
			Font = new Font( "Consolas", 9f ),
			BorderStyle = BorderStyle.FixedSingle,
			ScrollBars = RichTextBoxScrollBars.Vertical,
			WordWrap = true
		};

		var actionPanel = new FlowLayoutPanel
		{
			Dock = DockStyle.Bottom,
			Height = 38,
			FlowDirection = FlowDirection.LeftToRight,
			Padding = new Padding( 0, 4, 0, 0 )
		};

		_btnRun = new Button { Text = "Run Step", AutoSize = true, Height = 28 };
		_btnRun.Click += async ( _, _ ) => await OnRunStepAsync();

		_btnMarkDone = new Button { Text = "Mark Done", AutoSize = true, Height = 28 };
		_btnMarkDone.Click += ( _, _ ) => OnMarkDone();

		_btnSkip = new Button { Text = "Skip Step", AutoSize = true, Height = 28, FlatStyle = FlatStyle.Flat };
		_btnSkip.FlatAppearance.BorderColor = Color.FromArgb( 180, 180, 180 );
		_btnSkip.Click += ( _, _ ) => OnSkipStep();

		actionPanel.Controls.AddRange( 
			_btnRun, 
			_btnMarkDone, 
			_btnSkip,
			_outputBox,
			actionPanel,
			separator,
			_lblDesc,
			_lblBadges,
			_lblTitle
		);

		return panel;
	}

	private Panel BuildNavBar()
	{
		var bar = new Panel
		{
			Dock = DockStyle.Fill,
			BackColor = Color.FromArgb( 232, 232, 242 ),
			Padding = new Padding( 8, 8, 8, 8 )
		};

		_btnPrev = new Button { Text = "← Previous", AutoSize = true, Height = 28, Location = new Point( 8, 8 ) };
		_btnPrev.Click += ( _, _ ) => Navigate( -1 );

		_btnNext = new Button { Text = "Next →", AutoSize = true, Height = 28, Location = new Point( 100, 8 ) };
		_btnNext.Click += ( _, _ ) => Navigate( 1 );

		_btnFinish = new Button { Text = "Finish ✓", AutoSize = true, Height = 28, Enabled = false };
		_btnFinish.Click += ( _, _ ) => Close();

		_lblProgress = new Label
		{
			AutoSize = false,
			TextAlign = ContentAlignment.MiddleCenter,
			Font = new Font( "Segoe UI", 9f ),
			ForeColor = Color.FromArgb( 80, 80, 80 )
		};

		bar.Controls.AddRange( _btnPrev, _btnNext, _btnFinish, _lblProgress );

		bar.Resize += ( _, _ ) =>
		{
			_lblProgress.SetBounds( bar.Width / 2 - 80, 8, 160, 28 );
			_btnFinish.Location = new Point( bar.Width - _btnFinish.Width - 12, 8 );
		};

		return bar;
	}

	private void SelectStep( int index )
	{
		_current = index;
		_stepList.SelectedIndex = index;

		var step = _steps[ index ];
		var status = _statuses[ index ];

		_lblTitle.Text = step.Title;

		var badges = new List<string> { step.IsManual ? "MANUAL" : "AUTO" };

		if ( step.RequiresElevation ) badges.Add( "⚡ Requires Elevation (UAC)" );
		if ( !_applicable[ index ] ) badges.Add( "NOT APPLICABLE — will be skipped" );
		_lblBadges.Text = string.Join( "   ", badges );

		_lblDesc.Text = step.Description;
		_outputBox.Clear();

		var isComplete = status == StepStatus.Complete;
		var isSkipped = status == StepStatus.Skipped;
		var canAct = !_running && !isComplete && !isSkipped && _applicable[ index ];

		if ( isComplete )
			AppendOutput( "✓ This step is already complete.\r\n", Color.FromArgb( 0, 128, 64 ) );
		else if ( isSkipped )
			AppendOutput( "⊘ This step was skipped.\r\n", Color.Gray );
		else if ( !_applicable[ index ] )
			AppendOutput( "○ Not applicable on this machine — skipping.\r\n", Color.Gray );
		else if ( status == StepStatus.Failed )
			AppendOutput( "✗ Previous run failed. You can try again.\r\n", Color.Red );

		_btnRun.Visible = !step.IsManual && canAct;
		_btnRun.Enabled = !_running;
		_btnMarkDone.Visible = step.IsManual && canAct;
		_btnSkip.Visible = step.CanSkip && canAct;

		_btnPrev.Enabled = !_running && index > 0;
		_btnNext.Enabled = !_running && index < _steps.Count - 1;
		_lblProgress.Text = $"Step {index + 1} of {_steps.Count}";

		UpdateFinishButton();
	}

	private void Navigate( int delta )
	{
		var next = _current + delta;
		if ( next >= 0 && next < _steps.Count )
			SelectStep( next );
	}

	private async Task OnRunStepAsync()
	{
		if ( _running ) return;
		
		_running = true;
		SetNavEnabled( false );

		var step = _steps[ _current ];
		_outputBox.Clear();
		AppendOutput( $"Running: {step.Title}...\r\n\r\n", Color.FromArgb( 0, 102, 204 ) );
		SetStepStatus( _current, StepStatus.Running );

		var progress = new Progress<string>( msg =>
		{
			if ( InvokeRequired ) Invoke( () => AppendOutput( msg + "\r\n" ) );
			else AppendOutput( msg + "\r\n" );
		} );

		SetupStepResult result;
		try
		{
			result = await Task.Run( () => step.RunAsync( progress ) );
		}
		catch ( Exception ex )
		{
			result = new SetupStepResult( false, ex.Message );
		}

		AppendOutput( 
			$"\r\n{( result.Ok ? "✓" : "✗" )} {result.Message}\r\n",
			result.Ok ? Color.FromArgb( 0, 128, 64 ) : Color.Red 
		);

		SetStepStatus( _current, result.Ok ? StepStatus.Complete : StepStatus.Failed );
		_running = false;
		SetNavEnabled( true );
		SelectStep( _current );
	}

	private void OnMarkDone()
	{
		SetStepStatus( _current, StepStatus.Complete );
		SelectStep( _current );
		if ( _current < _steps.Count - 1 ) Navigate( 1 );
	}

	private void OnSkipStep()
	{
		SetStepStatus( _current, StepStatus.Skipped );
		SelectStep( _current );
		if ( _current < _steps.Count - 1 ) Navigate( 1 );
	}

	private void SetStepStatus( int index, StepStatus status )
	{
		_statuses[ index ] = status;
		_stepList.Invalidate( GetItemBounds( index ) );
	}

	private Rectangle GetItemBounds( int index )
	{
		if ( index < 0 || index >= _stepList.Items.Count ) return Rectangle.Empty;

		return _stepList.GetItemRectangle( index );
	}

	private void SetNavEnabled( bool enabled )
	{
		_btnPrev.Enabled = enabled && _current > 0;
		_btnNext.Enabled = enabled && _current < _steps.Count - 1;
		_btnRun.Enabled = enabled;
		_btnMarkDone.Enabled = enabled;
		_btnSkip.Enabled = enabled;
	}

	private void UpdateFinishButton()
	{
		_btnFinish.Enabled = 
			_statuses.Zip( 
				_applicable, 
				( s, a ) => !a || s == StepStatus.Complete || s == StepStatus.Skipped 
			).All( x => x );
	}

	private void AppendOutput( string text, Color? color = null )
	{
		_outputBox.SelectionStart = _outputBox.TextLength;
		_outputBox.SelectionLength = 0;
		_outputBox.SelectionColor = color ?? _outputBox.ForeColor;
		_outputBox.AppendText( text );
		_outputBox.ScrollToCaret();
	}

	private async Task CheckInitialStatusesAsync()
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
				continue;
			}

			var done = await step.IsAlreadyCompleteAsync();
			if ( done ) SetStepStatus( idx, StepStatus.Complete );
		}

		if ( !IsDisposed )
			Invoke( () =>
			{
				UpdateFinishButton();
				SelectStep( _current ); // refresh current step display
			} );
	}
}