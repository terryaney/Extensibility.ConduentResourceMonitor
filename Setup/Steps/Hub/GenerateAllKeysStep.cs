namespace ConduentResourceMonitor.Setup.Steps.Hub;

public class GenerateAllKeysStep( SetupContext ctx ) : ISetupStep
{
	private readonly SetupContext _ctx = ctx;

	public string Title => "Generate WireGuard Keys & Config Files";
	public bool RequiresElevation => false;
	public bool IsManual => false;
	public bool CanSkip => false;

	public string Description =>
		$"""
        Generates WireGuard key pairs for Hub and all Travel machines, then writes .conf files to:
        {_ctx.ConfDirectory}

        Files created:
          Hub-Tunnel.conf
        {string.Join( "\r\n", _ctx.TravelMachineNames.Select( n => $"  {n}-Tunnel.conf" ) )}

        Travel machines copy their .conf file to install their tunnel.
        """;

	public Task<bool> IsAlreadyCompleteAsync()
	{
		if ( !File.Exists( Path.Combine( _ctx.ConfDirectory, "Hub-Tunnel.conf" ) ) ) return Task.FromResult( false );
		foreach ( var name in _ctx.TravelMachineNames )
		{
			if ( !File.Exists( Path.Combine( _ctx.ConfDirectory, $"{name}-Tunnel.conf" ) ) )
				return Task.FromResult( false );
		}
		return Task.FromResult( true );
	}

	public async Task<SetupStepResult> RunAsync( IProgress<string> progress )
	{
		try
		{
			Directory.CreateDirectory( _ctx.ConfDirectory );

			progress.Report( "Generating Hub key pair..." );
			var hubPrivKey = await WgGenKey();
			var hubPubKey = await WgPubKey( hubPrivKey );
			progress.Report( $"Hub public key: {hubPubKey}" );

			var travelKeys = new List<(string Name, string PrivKey, string PubKey, string Ip)>();
			for ( var i = 0; i < _ctx.TravelMachineNames.Count; i++ )
			{
				var name = _ctx.TravelMachineNames[ i ];
				progress.Report( $"Generating key pair for {name}..." );
				var privKey = await WgGenKey();
				var pubKey = await WgPubKey( privKey );
				var ip = $"10.0.0.{i + 2}";
				travelKeys.Add( (name, privKey, pubKey, ip) );
				progress.Report( $"{name} public key: {pubKey}" );
			}

			// Write Hub-Tunnel.conf
			var hubConf = BuildHubConf( hubPrivKey, travelKeys );
			var hubConfPath = Path.Combine( _ctx.ConfDirectory, "Hub-Tunnel.conf" );
			File.WriteAllText( hubConfPath, hubConf );
			progress.Report( $"Written: {hubConfPath}" );

			// Write each Travel .conf
			foreach ( var (name, privKey, _, ip) in travelKeys )
			{
				var travelConf = BuildTravelConf( privKey, ip, hubPubKey );
				var travelConfPath = Path.Combine( _ctx.ConfDirectory, $"{name}-Tunnel.conf" );
				File.WriteAllText( travelConfPath, travelConf );
				progress.Report( $"Written: {travelConfPath}" );
			}

			return new SetupStepResult( true, "All config files generated successfully." );
		}
		catch ( Exception ex )
		{
			return new SetupStepResult( false, ex.Message );
		}
	}

	private static async Task<string> WgGenKey()
	{
		var (exitCode, output) = await ProcessHelper.RunAsync( "wg", "genkey" );
		return ProcessHelper.ValidateWireGuardKeyOutput( "wg genkey", exitCode, output, "private" );
	}

	private static async Task<string> WgPubKey( string privateKey )
	{
		var (exitCode, output) = await ProcessHelper.RunWithInputAsync( "wg", "pubkey", privateKey );
		return ProcessHelper.ValidateWireGuardKeyOutput( "wg pubkey", exitCode, output, "public" );
	}

	private static string BuildHubConf( string hubPrivKey, List<(string Name, string PrivKey, string PubKey, string Ip)> travels )
	{
		var sb = new System.Text.StringBuilder();

		sb.AppendLine( "[Interface]" );
		sb.AppendLine( $"PrivateKey = {hubPrivKey}" );
		sb.AppendLine( "ListenPort = 51820" );
		sb.AppendLine( "Address = 10.0.0.1/32" );

		foreach ( var (name, _, pubKey, ip) in travels )
		{
			sb.AppendLine();
			sb.AppendLine( "[Peer]" );
			sb.AppendLine( $"# {name}" );
			sb.AppendLine( $"PublicKey = {pubKey}" );
			sb.AppendLine( $"AllowedIPs = {ip}/32" );
		}
		return sb.ToString();
	}

	private string BuildTravelConf( string travelPrivKey, string travelIp, string hubPubKey ) => $"""
        [Interface]
        PrivateKey = {travelPrivKey}
        Address = {travelIp}/32

        [Peer]
        PublicKey = {hubPubKey}
        AllowedIPs = 10.0.0.1/32
        Endpoint = {_ctx.HubPublicIp}:51820
        PersistentKeepalive = 25
        """;
}