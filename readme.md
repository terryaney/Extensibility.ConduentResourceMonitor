# Conduent Resource Sharing Setup

Terms Used Throughout Instructions:

- `Hub` - Represents the always-on machine that will listen for incoming WireGuard requests.
- `Travel` - Represents any machine that will be outside the LAN and needs to connect back to `Hub`'s LAN
- `Resource` - Represents Conduent laptop that shares VPN resource with `Hub` and `Travel` PCs.
- `10.0.0.*` Subnet - Was just chosen as 'safe' IPs because my router issues IPs of `192.168.158.*`.  You may need to modify based on your router.

The following sections will help setup everything needed to share VPN from Conduent Laptop to personal `Hub` and `Travel` machines.

**Important** - Once all steps are complete and have been tested a RDP session from `Hub` to `Resource` must always be open to keep Global Connect VPN alive.  Even with this RDP enabled, note that the VPN disconnected periodically and requires manual reconnect action.

## Router Setup

1. Create static leases for following machines noting down IPs for later configuration.
  - `Hub` - In my case it is `192.168.158.2` (needed for WireGuard port communication function between `Hub` and `Travel`)
  - `Resource` - In my case it is `192.168.158.3` (needed for stable `hosts` file entry)
  - **Note**: AmpliFi mobile application allows you to find `Hub` and `Resource` under 'Clients' tab and simply click 'Create Static Lease'.

2. Enable port forwarding of `51820` to the static lease IP created in previous step (if there is incoming and destination ports required in settings, use `51820` for both).

## WireGuard Setup

1. [Install WireGuard](https://www.wireguard.com/install/) on `Hub` and `Travel`

2. Add empty tunnel on `Hub` **and** `Travel` 
  - This will generate private and public keys.
	- Name each tunnel whatever you want, but take note of name, referred to below as `<Hub-Tunnel-Name>` and `<Travel-Tunnel-Name>` (in my case `Hub-Tunnel` and `Travel-Tunnel`).

3. Edit `Hub` tunnel configuration as follows:
	* You can get your `Hub` public IP via [whatismyipaddress.com](whatismyipaddress.com)
	```
	[Interface]
	PrivateKey = <hub-private-key>
	ListenPort = 51820
	Address = 10.0.0.1/32

	[Peer]
	PublicKey = <travel-public-key>
	AllowedIPs = 10.0.0.2/32
	```

4. Edit `Travel` tunnel configuration as follows:
	```
	[Interface]
	PrivateKey = <travel-private-key>
	Address = 10.0.0.2/32

	[Peer]
	PublicKey = <hub-public-key>
	AllowedIPs = 10.0.0.1/32
	Endpoint = <hub-public-ip>:51820
	PersistentKeepalive = 25
	```

5. If you have more than one `Travel` machine, for each machine...
  - Create empty tunnel to get private and public keys
  - Repeat step 4, but increment `Interface.Address` by one each time
	- Add another `Peer` entry in `Hub` configuration for new public key and IP.

6. Install tunnels as a service on `Hub` and `Travel` machines
	- From WireGuard, export tunnel to zip.  Extract and copy the `<Hub-Tunnel-Name>.conf` (note, I use `Hub-Tunnel.conf` and `Travel-Tunnel.conf`) to `C:\BTR\Extensibility\ConduentResource\<Hub-Tunnel-Name>.conf`.
	- In Adminstrative Mode:
		- `CMD.exe`: Run `"C:\Program Files\WireGuard\wireguard.exe" /installtunnelservice "C:\BTR\Extensibility\ConduentResource\<Hub-Tunnel-Name>.conf"`
		- PowerShell: Run `& "C:\Program Files\WireGuard\wireguard.exe" /installtunnelservice "C:\BTR\Extensibility\ConduentResource\<Hub-Tunnel-Name>.conf"`
	- If you need to uninstall, simply type `"C:\Program Files\WireGuard\wireguard.exe" /uninstalltunnelservice <Hub-Tunnel-Name>`
	- **NOTE**: If WireGuard is manually opened, it'll show the status of the 'Service' and most likely is read-only.  But when you close the application (and use `Exit` from system tray icon - or probably shutdown) it automatically uninstalls the service, so you'll have to re-install the service any time you work with application manually.
  - To check to see if the tunnel is active, you can use the `wg show` command.

At this point, with WireGuard tunnels activated, you should be able to `ping 10.0.0.1` from the `Travel` machine(s) and get a response.

## Firewall Setup

On the `Hub` firewall settings perform the following steps.

1. Add rules to allow `Travel` access
	- Open via `Win+R` and run `wf.msc`
	- Under Inbound Rules, find `File and Printer Sharing (Echo Request - ICMPv4-In)`. 
		- Double clicked Private profile row (which was enabled)
		- Under Advanced tab, click/enabled all three profiles (Domain/Private/Public)
		- Under the Scope tab, add the `Travel` address(es) (`10.0.0.2`) to the 'Remote IP addresses' list (if you have multiple `Travel` machine, you need to add all IPs here).

2. From administrative prompt, create 2 firewall rules to allow incoming traffic on ports 8888 and 13389
      ```
      netsh advfirewall firewall add rule name="WireGuard Port 8888" protocol=TCP dir=in localport=8888 action=allow
      netsh advfirewall firewall add rule name="WireGuard Port 13389" protocol=TCP dir=in localport=13389 action=allow
      ```

3. From administrative prompt, run `netsh` commands to enable port forwarding.
  - Create Port Forward from Hub:8888 -> Resource:8888 to support VPN sharing directly on 'Travel' when WireGuard enabled.
	- Create Port Forward from Hub:13389 -> Resource:3389 support direct RDP from 'Travel' -> 'Resource' (LAN - localhost:13389, WAN - 10.0.0.1:13389)
      ```
      netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=8888 connectaddress=conduent-resource connectport=8888
      netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=13389 connectaddress=conduent-resource connectport=3389
      ```
	- **Note**: Idea on RDP ports is to support requests *from* `Travel`, if you hit `3389` on `Hub` (i.e. when on LAN) it connects to `Hub` in RDP as expected.  But when `13389` is hit on `Hub` (i.e. RDP to `10.0.0.1:13389` when on LAN) it forwards it to `3389` on `Resource` so RDP connects directly to that.

### Useful Firewall Management Commands

```
# Reset port forwarding
netsh interface portproxy reset

# View all proxy rules
netsh interface portproxy show all

# Verify 8888 is listening, should receive: '0.0.0.0:8888 LISTENING'
netstat -an | findstr 8888

# Remove the rule when done
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=8888
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=13389

# Verify firewall port is open
netsh advfirewall firewall show rule name="WireGuard Port 8888"
netsh advfirewall firewall show rule name="WireGuard Port 13389"

# Remove firewall rule
netsh advfirewall firewall delete rule name="WireGuard Port 8888"
netsh advfirewall firewall delete rule name="WireGuard Port 13389"
```

## Hosts File Setup

In `Hub` and `Travel` machines, edit `C:\Windows\System32\drivers\etc\hosts` and add entry for `conduent-resource`.
  - `Hub`: `192.168.158.3 conduent-resource` (Static Lease IP above for `Resource` machine).
	- `Travel`: `10.0.0.1 conduent-resource`

At this point, with WireGuard tunnels activated, you should be able to
1. `ping conduent-resource` from the `Travel` machine(s) and get a response
2. Start a RDP session to `conduent-resource:13389` and successfully connect

## Git Proxy Setup

**Note**: If leveraging git configuration from [Extensibility.Git.Configuration](https://github.com/terryaney/Extensibility.Git.Configuration.git)...

1. `%USERPROFILE%\.gitconfig` simply references `C:\BTR\Extensibility\Git.Configuration\.gitconfg`
2. Proxy configuration can not be placed in the `C:\BTR\Extensibility\Git.Configuration\.gitconfg` file because not all my machines need the proxy set (i.e. Conduent VDI or Conduent Resource machine).
3. On `Hub` and `Travel` machines, manually edit `%USERPROFILE%\.gitconfig` so it looks like
    ```
    [include]
      path = C:/BTR/Extensibility/Git.Configuration/.gitconfig
    
    [http "https://tfs.acsgs.com"]
    	proxy = http://conduent-resource:8888	
    ```
 
If **not** leveraging repository configuration, simply run the following command:

`git config --global http.https://tfs.acsgs.com.proxy http://conduent-resource:8888`



## Proxy Resource Setup

To enable personal machines to leverage the VPN of `Resource` do the following steps on appropriate machines.  

### Terminal Profiles

The following profile is needed on the `Resource` machine only.  From Windows Terminal, open Settings.  Then click the `Open JSON file` in lower left status bar to add the profile below.

```json
{
		"background": "#08082E",
		"backgroundImage": "C:\\BTR\\Extensibility\\PowerShell\\Icons\\vpn.png",
		"backgroundImageAlignment": "bottomRight",
		"backgroundImageOpacity": 0.1,
		"backgroundImageStretchMode": "none",
		"commandline": "pwsh.exe -NoExit -Command \"Write-Host 'DO NOT CLOSE this Terminal tab, it is needed for VPN support.' -ForegroundColor Yellow; pproxy -l http://:8888\"",
		"guid": "{7c2d8c34-4f7a-4d1f-8f5d-8b7c4b6b9cda}",
		"hidden": false,
		"icon": "C:\\BTR\\Extensibility\\PowerShell\\Icons\\vpn.png",
		"name": "Conduent-Resource - Resource Provider",
		"startingDirectory": "C:\\BTR\\Extensibility\\PowerShell"
},
```

### .pac File Setup

On the `Hub` and `Travel` machines, perform the following steps.

1. Save the following content to `C:\BTR\Extensibility\ConduentResource\conduent-resource.pac`.
    ```javascript
    function FindProxyForURL(url, host) {
        // Convert to lowercase for case-insensitive matching
        host = host.toLowerCase();
        url = url.toLowerCase();
        
    	if (host == "hrsuappba7003" || // Exact hostname match for internal server
    		shExpMatch(host, "*.acsgs.com") || // Domain-based matches
    		shExpMatch(host, "*.int.benefitcenter.com") || 
    		shExpMatch(host, "*.americas.oneacs.com") || 
            shExpMatch(host, "*.securep.benefitcenter.com")) {
            return "PROXY conduent-resource:8888";
        }
        
        // Direct connection for everything else
        return "DIRECT";
    }
    ```
2. Install Python — the Resource Monitor uses it to serve the `.pac` file automatically.
  - `winget install Python.Python.3.12 --source winget`, restart Terminal
3. In Settings > Network & internet > Proxy, set 'Use setup script' to: `http://localhost:8080/conduent-resource.pac`
  - **NOTE**: If any change in content or location occurs in the `.pac` file, Chrome usually retains a cached copy.  [chrome://net-internals/#proxy](chrome://net-internals/#proxy) can be used to clear the cache and reset the proxy information. 

### pproxy Setup

On `Resource` machine, from an **administrative** terminal, apply run the following instructions.

1. `winget install Python.Python.3.12 --source winget`, restart Terminal
1. `pip install pproxy`, restart Terminal
1. `New-NetFirewallRule -DisplayName "pproxy" -Direction Inbound -LocalPort 8888 -Protocol TCP -Action Allow`

### Developer Settings Setup

From the `Hub` and `Travel` machines, the Camelot `WebService.Proxy` Api project needs to leverage `conduent-resource` proxy. 

Create a `C:\BTR\GlobalConfiguration\CamelotSettings.Api.WebService.Proxy.json` file with following content:  
	```json-with-comments
	{
		"TheKeep": {
			"Endpoints": {
				/* This isn't shared with other developers, but I need so I can debug proxy api calls using vpn passthrough */
				"ProxyServer": {
					"Url": "http://conduent-resource:8888",
					"BypassExpressions": [
						"http://(?!HRSUAPPBA7003)|127\\.0\\.0\\.1|localhost|mymedicalshopper|api.telegram"
					]
				}
			}
		}
	}
	```

## Resource Monitor

`ConduentResourceMonitor.exe` is a system tray application that monitors the tunnel infrastructure on `Hub` and `Travel` machines. It shows a green circle when all checks pass and red when any fail. Hover text shows per-item status. A Windows notification fires on each new failure.

The exe and its settings files live in `C:\BTR\Extensibility\ConduentResourceMonitor\`.

### What Each Mode Monitors

| Check | Hub | Travel |
|---|---|---|
| pproxy / VPN | HTTP to internal Conduent URL via `conduent-resource:8888` | same |
| Port Forward | TCP connect to `localhost:8888` and `localhost:13389` | TCP connect to `conduent-resource:13389` |
| PAC Server | — | HTTP to `localhost:8080/conduent-resource.pac` |
| WireGuard | `wg show` for active tunnel | same |

Both modes start and own the Python PAC server (`python -m http.server`) on launch, killing it cleanly on exit.

### Command Line Options

| Option | Description |
|---|---|
`--mode` | Hub or Travel. If omitted, inferred from settings file when exactly one exists.
`--repair-on-start` | Hub only. Immediately runs port proxy repair on launch (use for startup shortcut).
`--check-url` | URL for VPN/pproxy health check. Default: https://hrspwebtools001.americas.oneacs.com/msl
`--tunnel-name` | WireGuard tunnel/service name. Default: Hub-Tunnel or Travel-Tunnel
`--pac-dir` | Directory containing conduent-resource.pac. Default: C:\BTR\Extensibility\ConduentResource
`--pac-port` | PAC HTTP server port. Default: 8080
`--check-interval` | Seconds between checks. Default: 30
`--notify-timeout` | Notification display time in ms. Default: 5000
`--show-log` | Open the log window on startup

### Settings

All options are also configurable via right-click → **Settings**. Settings are persisted to `ResourceMonitor.Hub.settings.json` or `ResourceMonitor.Travel.settings.json` next to the exe. Command line args override settings at runtime without writing back.

### Right-Click Actions

Fix actions appear only when the corresponding check is failing:

- **Fix: Repair Port Forwarding** — Hub only. Runs an embedded port proxy repair script elevated (UAC prompt). Stops/starts the IP Helper service and re-applies the `netsh portproxy` rules.
- **Fix: Restart WireGuard** — Restarts the WireGuard tunnel service elevated (UAC prompt).
- **Fix: Restart PAC Server** — Travel only. Kills and restarts the Python PAC server process.

## Start Up Setup

All startup links are created in `~\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup`.

### `Resource` Start Up Setup

Create a shortcut to launch `pproxy` via the `Conduent-Resource - Resource Provider` terminal profile.

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$shell = New-Object -ComObject WScript.Shell
$wt = (Get-Command wt.exe).Source

$lnk = $shell.CreateShortcut("$startup\Conduent-Resource - Resource Provider.lnk")
$lnk.TargetPath = $wt
$lnk.Arguments = '-p "Conduent-Resource - Resource Provider"'
$lnk.WorkingDirectory = 'C:\BTR\Extensibility\PowerShell'
$lnk.IconLocation = 'C:\BTR\Extensibility\PowerShell\Icons\vpn.png'
$lnk.Save()
```

### `Hub` Start Up Setup

Create a single startup shortcut for the Resource Monitor with `--repair-on-start`.

**Note**: `netsh portproxy` rules persist across reboots but the TCP listener binding fails silently — a known Windows timing issue where portproxy tries to bind before the network stack is fully ready. The `--repair-on-start` flag handles this automatically: the monitor fires the repair script with a 60-second startup delay, then continues monitoring. The tray icon will show red for PortFwd during that window.

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$shell = New-Object -ComObject WScript.Shell
$monitorDir = "C:\BTR\Extensibility\ConduentResource"

$lnk = $shell.CreateShortcut("$startup\Conduent Resource Monitor - Hub.lnk")
$lnk.TargetPath = "$monitorDir\ConduentResourceMonitor.exe"
$lnk.Arguments = '--mode Hub --repair-on-start'
$lnk.WorkingDirectory = $monitorDir
$lnk.IconLocation = 'C:\BTR\Extensibility\PowerShell\Icons\vpn.png'
$lnk.Save()
```

### `Travel` Start Up Setup

Create a single startup shortcut for the Resource Monitor.

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$shell = New-Object -ComObject WScript.Shell
$monitorDir = "C:\BTR\Extensibility\ConduentResource"

$lnk = $shell.CreateShortcut("$startup\Conduent Resource Monitor - Travel.lnk")
$lnk.TargetPath = "$monitorDir\ConduentResourceMonitor.exe"
$lnk.Arguments = '--mode Travel'
$lnk.WorkingDirectory = $monitorDir
$lnk.IconLocation = 'C:\BTR\Extensibility\PowerShell\Icons\vpn.png'
$lnk.Save()
```
