namespace ConduentResourceMonitor;

public class LogForm : Form
{
	private readonly TextBox _textBox;
	private const int MaxLines = 1000;

	public LogForm()
	{
		Text = "Resource Monitor Log";
		Size = new Size( 750, 420 );
		StartPosition = FormStartPosition.CenterScreen;

		_textBox = new TextBox
		{
			Multiline = true,
			ReadOnly = true,
			ScrollBars = ScrollBars.Both,
			Dock = DockStyle.Fill,
			Font = new Font( "Consolas", 9 ),
			BackColor = Color.Black,
			ForeColor = Color.LimeGreen,
			WordWrap = false
		};
		Controls.Add( _textBox );
	}

	public void AppendLine( string line )
	{
		if ( InvokeRequired ) { Invoke( () => AppendLine( line ) ); return; }

		var lines = _textBox.Lines.ToList();
		lines.Add( line );
		
		if ( lines.Count > MaxLines ) lines.RemoveAt( 0 );
		
		_textBox.Lines = [ .. lines ];
		_textBox.SelectionStart = _textBox.Text.Length;
		_textBox.ScrollToCaret();
	}

	protected override void OnFormClosing( FormClosingEventArgs e )
	{
		if ( e.CloseReason == CloseReason.UserClosing )
		{
			e.Cancel = true;
			Hide();
		}
		else
		{
			base.OnFormClosing( e );
		}
	}
}