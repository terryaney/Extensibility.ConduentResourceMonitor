namespace ConduentResourceMonitor.Checks;

public class PacServerCheck( AppSettings settings ) : ICheck
{
	private readonly AppSettings _settings = settings;
	private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds( 5 ) };

	public string Name => "PAC Web Server";

	public async Task<CheckResult> RunAsync()
	{
		try
		{
			var response = await _client.GetAsync( _settings.PacUrl );
			return new CheckResult( Name, true, $"HTTP {(int)response.StatusCode}" );
		}
		catch ( Exception ex )
		{
			return new CheckResult( Name, false, ex.InnerException?.Message ?? ex.Message );
		}
	}
}
