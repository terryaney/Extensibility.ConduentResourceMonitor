# Setup Reference

This document covers two things:

1. **What the setup wizard automates** — step tables for each mode showing what runs, what requires elevation, and how completion is detected.
2. **Manual setup instructions** — the full step-by-step reference for anyone who needs to do things by hand or wants to understand what the wizard is doing.

---

## What the Wizard Does

The `--setup` wizard walks through each step, checks whether it's already complete (idempotent), and only runs steps that need action. Steps that require administrator access show a UAC prompt. Manual steps show instructions with a "Mark Done" button.

### Hub Steps

| # | Step | Type | Elevation | Already-Complete Check |
|---|---|---|---|---|
| 0 | Router Configuration | Manual | — | Always shown (cannot verify) |
| 1 | Install WireGuard | Auto | No | `wg --version` exits 0 |
| 2 | Generate WireGuard Keys & Config Files | Auto | No | All expected `.conf` files exist in conf dir |
| 3 | Install Hub Tunnel Service | Auto | Yes | Service `WireGuardTunnel$Hub-Tunnel` exists |
| 4 | Configure Firewall Rules | Auto | Yes | `netsh advfirewall firewall show rule name="WireGuard Port 8888"` returns data |
| 5 | Configure Port Proxy Rules | Auto | Yes | `netsh interface portproxy show all` contains 8888 and 13389 |
| 6 | Update Hosts File | Auto | Yes | `hosts` file contains `conduent-resource` entry with Resource IP |
| 7 | Configure Git Proxy | Auto | No | `git config --global` value set; **skipped** if git not installed |
| 8 | Install Python | Auto | No | `python --version` ≥ 3.12 |
| 9 | Create PAC File | Auto | No | `conduent-resource.pac` exists in conf dir |
| 10 | Configure Windows Proxy Settings | Auto | No | `AutoConfigURL` registry value set in `HKCU\...\Internet Settings` |
| 11 | Create Startup Shortcut | Auto | No | Shortcut `.lnk` exists in `%APPDATA%\...\Startup` |
| 12 | Travel Config File Locations | Manual | — | Auto-complete when all Travel `.conf` files exist |

Step 1 is skipped if **Skip WireGuard** is checked in the pre-flight dialog (for LAN-only setups with no remote access needed).

All WireGuard keys are generated on Hub in step 2 using `wg genkey` / `wg pubkey`. Output:
- `Hub-Tunnel.conf` — Hub's private key + one `[Peer]` per Travel machine
- `MachineName-Tunnel.conf` per Travel machine — Travel private key + Hub public key + endpoint

### Travel Steps

Pre-flight hard block: the `.conf` file (generated on Hub) must exist and be a valid WireGuard config before the wizard opens.

| # | Step | Type | Elevation | Already-Complete Check |
|---|---|---|---|---|
| 1 | Install WireGuard | Auto | No | `wg --version` exits 0 |
| 2 | Verify / Copy Config File | Auto | No | `.conf` file exists in conf dir |
| 3 | Install Travel Tunnel Service | Auto | Yes | Service `WireGuardTunnel$<name>` exists |
| 4 | Update Hosts File | Auto | Yes | `hosts` contains `10.0.0.1  conduent-resource` |
| 5 | Configure Git Proxy | Auto | No | `git config --global` value set; **skipped** if git not installed |
| 6 | Install Python | Auto | No | `python --version` ≥ 3.12 |
| 7 | Create PAC File | Auto | No | `conduent-resource.pac` exists in conf dir |
| 8 | Configure Windows Proxy Settings | Auto | No | `AutoConfigURL` registry value set in `HKCU\...\Internet Settings` |
| 9 | Create Startup Shortcut | Auto | No | Shortcut `.lnk` exists in Startup folder |

### Resource Steps

| # | Step | Type | Elevation | Already-Complete Check |
|---|---|---|---|---|
| 1 | Install Python | Auto | No | `python --version` ≥ 3.12 |
| 2 | Install pproxy | Auto | No | `pip show pproxy` exits 0 |
| 3 | Configure pproxy Firewall Rule | Auto | Yes | `netsh advfirewall firewall show rule name="pproxy"` returns data |
| 4 | Add Windows Terminal Profile | Auto | No | Profile GUID `{7c2d8c34-...}` found in `settings.json`; **hard block** if `wt.exe` not in PATH |
| 5 | Create Startup Shortcut | Auto | No | Shortcut `.lnk` exists in Startup folder |
| 6 | Create Developer Settings File | Auto | No | `C:\BTR\GlobalConfiguration\CamelotSettings.Api.WebService.Proxy.json` exists |

### --add-travel-config

Separate dialog (not part of the main wizard). Generates a new key pair for an additional Travel machine, appends the `[Peer]` block to `Hub-Tunnel.conf`, reinstalls the Hub tunnel service, and writes the new `MachineName-Tunnel.conf`. Auto-suggests the next available `10.0.0.x` address by scanning existing `.conf` files.

---

## Manual Setup Instructions

The sections below document everything the wizard handles. Use these if you need to set up manually, recover from a partial install, or understand what a step is doing.

### Terms

- `Hub` — Always-on home machine that listens for WireGuard requests.
- `Travel` — Any machine used outside the home LAN that connects back to Hub.
- `Resource` — Conduent laptop sharing its corporate VPN.
- `10.0.0.*` — WireGuard subnet (chosen because home router uses `192.168.158.*`).

**Important:** Once everything is set up, a RDP session from `Hub` to `Resource` must always be open to keep Global Connect VPN alive. The VPN disconnects periodically and requires manual reconnect.

---

### Router Setup

*Cannot be automated — must be done manually on the router.*

1. Create static leases for:
   - `Hub` — e.g. `192.168.158.2` (needed for WireGuard port forwarding between Hub and Travel)
   - `Resource` — e.g. `192.168.158.3` (needed for stable `hosts` file entry)
   - **Note**: AmpliFi: Clients tab → machine → Create Static Lease.

2. Forward port `51820` (UDP) to Hub's static lease IP (use `51820` for both incoming and destination if the router requires both).

---

### WireGuard Setup

The wizard handles this automatically on Hub and Travel. Manual steps below if needed.

1. [Install WireGuard](https://www.wireguard.com/install/) on `Hub` and `Travel`.

2. Generate keys. The wizard uses `wg genkey` and `wg pubkey`. Manually you can add an empty tunnel in the WireGuard UI to generate keys, or run:
   ```
   wg genkey > private.key
   wg pubkey < private.key > public.key
   ```

3. `Hub-Tunnel.conf`:
   ```
   [Interface]
   PrivateKey = <hub-private-key>
   ListenPort = 51820
   Address = 10.0.0.1/32

   [Peer]
   PublicKey = <travel-public-key>
   AllowedIPs = 10.0.0.2/32
   ```
   Add one `[Peer]` block per Travel machine, incrementing the IP (`10.0.0.3`, etc.).

4. `MachineName-Tunnel.conf` (Travel):
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

5. Install as a Windows service (run in an administrative prompt):
   ```
   "C:\Program Files\WireGuard\wireguard.exe" /installtunnelservice "C:\BTR\Extensibility\ConduentResource\Hub-Tunnel.conf"
   ```
   Uninstall: `wireguard.exe /uninstalltunnelservice Hub-Tunnel`

   **Note:** Opening the WireGuard UI and then closing it (Exit from system tray) automatically uninstalls the service. Re-run the install command after working with WireGuard manually.

6. Verify: `wg show` — should show the tunnel interface. `ping 10.0.0.1` from Travel should get a response.

---

### Firewall Setup

Run these from an **administrative** prompt on `Hub`.

**Inbound rules for ports 8888 and 13389:**
```
netsh advfirewall firewall add rule name="WireGuard Port 8888" protocol=TCP dir=in localport=8888 action=allow
netsh advfirewall firewall add rule name="WireGuard Port 13389" protocol=TCP dir=in localport=13389 action=allow
```

**Port proxy rules** (forwards Hub ports to Resource):
```
netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=8888 connectaddress=conduent-resource connectport=8888
netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=13389 connectaddress=conduent-resource connectport=3389
```
- `Hub:8888` → `Resource:8888` — VPN proxy sharing for Travel
- `Hub:13389` → `Resource:3389` — RDP to Resource from Travel (`10.0.0.1:13389`)

**ICMPv4 (ping from Travel):**
Open `wf.msc` → Inbound Rules → find `File and Printer Sharing (Echo Request - ICMPv4-In)` → Advanced tab → enable all three profiles → Scope tab → add Travel IPs (`10.0.0.2`, etc.) to Remote IP addresses.

#### Useful Firewall Commands

```
# View all port proxy rules
netsh interface portproxy show all

# Verify port is listening
netstat -an | findstr 8888

# Reset all port proxy rules
netsh interface portproxy reset

# Remove individual rules
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=8888
netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=13389

# Check firewall rule
netsh advfirewall firewall show rule name="WireGuard Port 8888"

# Remove firewall rule
netsh advfirewall firewall delete rule name="WireGuard Port 8888"
netsh advfirewall firewall delete rule name="WireGuard Port 13389"
```

---

### Hosts File Setup

Edit `C:\Windows\System32\drivers\etc\hosts` (requires administrator) and add:

- **Hub**: `192.168.158.3  conduent-resource` (Resource's static lease IP)
- **Travel**: `10.0.0.1  conduent-resource` (Hub's WireGuard IP)

Verify: `ping conduent-resource` and `mstsc /v:conduent-resource:13389` should work from Travel.

---

### Git Proxy Setup

If using [Extensibility.Git.Configuration](https://github.com/terryaney/Extensibility.Git.Configuration.git):

`%USERPROFILE%\.gitconfig` references `C:\BTR\Extensibility\Git.Configuration\.gitconfig`. The proxy cannot go there (not all machines need it). On `Hub` and `Travel`, manually edit `%USERPROFILE%\.gitconfig`:

```ini
[include]
  path = C:/BTR/Extensibility/Git.Configuration/.gitconfig

[http "https://tfs.acsgs.com"]
  proxy = http://conduent-resource:8888
```

Without the shared config, run:
```
git config --global http.https://tfs.acsgs.com.proxy http://conduent-resource:8888
```

---

### pproxy Setup

On `Resource`, from an **administrative** terminal:

```
winget install Python.Python.3.12 --source winget
```
Restart terminal, then:
```
pip install pproxy
New-NetFirewallRule -DisplayName "pproxy" -Direction Inbound -LocalPort 8888 -Protocol TCP -Action Allow
```

---

### Windows Terminal Profile (Resource)

From Windows Terminal → Settings → `Open JSON file`, add to `profiles.list`:

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
}
```

---

### PAC File Setup

The wizard creates this file automatically. Content of `C:\BTR\Extensibility\ConduentResource\conduent-resource.pac`:

```javascript
function FindProxyForURL(url, host) {
    host = host.toLowerCase();
    url = url.toLowerCase();

    if (host == "hrsuappba7003" ||
        shExpMatch(host, "*.acsgs.com") ||
        shExpMatch(host, "*.int.benefitcenter.com") ||
        shExpMatch(host, "*.americas.oneacs.com") ||
        shExpMatch(host, "*.securep.benefitcenter.com")) {
        return "PROXY conduent-resource:8888";
    }

    return "DIRECT";
}
```

Install Python (for the PAC server that the monitor runs):
```
winget install Python.Python.3.12 --source winget
```

The wizard sets Windows proxy on both Hub and Travel via the "Configure Windows Proxy Settings" step: **Settings → Network & internet → Proxy → Use setup script** → `http://localhost:8080/conduent-resource.pac`.

**Note:** Chrome caches proxy settings. Visit `chrome://net-internals/#proxy` to clear if needed.

---

### Developer Settings (Hub and Travel)

Create `C:\BTR\GlobalConfiguration\CamelotSettings.Api.WebService.Proxy.json`:

```json
{
  "TheKeep": {
    "Endpoints": {
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

---

### Startup Shortcuts

All shortcuts go in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`.

**Resource** — launches pproxy via Windows Terminal:
```powershell
$startup = [Environment]::GetFolderPath('Startup')
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut("$startup\Conduent-Resource - Resource Provider.lnk")
$lnk.TargetPath = (Get-Command wt.exe).Source
$lnk.Arguments = '-p "Conduent-Resource - Resource Provider"'
$lnk.WorkingDirectory = 'C:\BTR\Extensibility\PowerShell'
$lnk.IconLocation = 'C:\BTR\Extensibility\PowerShell\Icons\vpn.png'
$lnk.Save()
```

**Hub** — launches monitor with repair-on-start:
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

**Note:** `netsh portproxy` rules persist across reboots but the TCP listener binding fails silently on boot — a known Windows timing issue. `--repair-on-start` fires the repair script with a 60-second delay, handling this automatically. The tray icon will show red for PortFwd during that window.

**Travel** — launches monitor:
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
