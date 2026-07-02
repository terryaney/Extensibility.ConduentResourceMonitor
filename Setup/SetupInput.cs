namespace ConduentResourceMonitor.Setup;

public enum SetupInputKind { Text, FilePath, FolderPath, MultilineText }

// A value a step collects from the user. The wizard renders each step's inputs as stacked
// label/textbox rows (with a browse or fetch button per Kind/AutoFetch), validates them when
// Next is clicked, and applies them via Set before the step runs.
public class SetupInput
{
	public required string Label { get; init; }
	public SetupInputKind Kind { get; init; } = SetupInputKind.Text;
	public string Placeholder { get; init; } = "";
	public string FileFilter { get; init; } = "All Files|*.*";
	public required Func<string> Get { get; init; }
	public required Action<string> Set { get; init; }
	public Func<string, string?>? Validate { get; init; }
	// When set, the wizard shows a ↺ button and auto-fetches once if the value is empty.
	public Func<Task<string>>? AutoFetch { get; init; }

	public static string? Required( string value, string label ) =>
		string.IsNullOrWhiteSpace( value ) ? $"{label} is required." : null;

	public static string? RequiredIPv4( string value, string label ) =>
		!System.Net.IPAddress.TryParse( value, out var ip ) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
			? $"{label} must be a valid IPv4 address."
			: null;
}
