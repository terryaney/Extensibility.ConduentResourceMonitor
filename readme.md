# Conduent Resource Monitor

## What This Does

Conduent issues a laptop (`Resource`) with corporate VPN access. `Hub` is an always-on personal machine on the home LAN. `Travel` machines are personal laptops or devices used off the home LAN (hotels, coffee shops, etc.).

This setup lets Hub and Travel machines reach corporate resources — internal URLs, TFS, tools — as if sitting on the corporate network:

- **pproxy** runs on `Resource` and exposes the corporate VPN as an HTTP proxy on port `8888`.
- **WireGuard** creates an encrypted tunnel between `Hub` and each `Travel` machine so they can reach the Hub LAN from anywhere.
- **Port forwarding** on `Hub` routes proxy and RDP traffic from the WireGuard tunnel to `Resource`.
- **PAC file** on `Hub` and `Travel` machines tells browsers which URLs to route through the proxy.

The result: `Hub` uses `conduent-resource:8888` for corporate traffic. `Travel` machines use the WireGuard tunnel to reach `Hub`, which forwards to `Resource`.

---

## Terms

| Term | Meaning |
|---|---|
| `Hub` | Always-on home machine that listens for WireGuard connections |
| `Travel` | Any machine used outside the home LAN that connects back via WireGuard |
| `Resource` | Conduent laptop sharing its corporate VPN |
| `10.0.0.*` | WireGuard subnet (chosen because home router uses `192.168.158.*`) |

**Important:** Once everything is set up, a RDP session from `Hub` to `Resource` must always be open to keep Global Connect VPN alive. The VPN still disconnects periodically and requires manual reconnect.

---

## Install

> **Before starting:** Complete router configuration — static leases for Hub and Resource, port 51820 (UDP) forwarded to Hub. These cannot be automated. See [Setup/readme.md](Setup/readme.md#router-setup) for instructions.

### Setup Order

Run these in order on each machine. Each command opens a guided wizard.

**0. All machines** — Create `C:\BTR\Extensibility\ConduentResource\` and copy `ConduentResourceMonitor.exe` into it.

**1. Resource** (on the Conduent laptop)
```
ConduentResourceMonitor.exe --setup Resource
```
Installs Python, pproxy, Windows Firewall rule, Windows Terminal profile, and startup shortcut.

**2. Hub** (on the always-on home machine)
```
ConduentResourceMonitor.exe --setup Hub
```
Collects Resource's IP, Hub's LAN IP, Hub's public IP, Travel machine names, and config directory. Generates all WireGuard keys centrally — no manual key exchange needed. Installs Hub tunnel service, firewall rules, port proxy rules, hosts file, PAC file, and startup shortcut.

**3. Travel** (on each remote machine — after copying its `.conf` file from Hub)
```
ConduentResourceMonitor.exe --setup Travel --conf-file "C:\BTR\Extensibility\ConduentResource\MachineName-Tunnel.conf"
```
Installs WireGuard tunnel service, updates hosts file, creates PAC file, configures Windows proxy settings, creates startup shortcut.

### Adding Travel Machines Later

On Hub:
```
ConduentResourceMonitor.exe --add-travel-config
```
Prompts for a machine name, generates a new key pair, appends the peer to `Hub-Tunnel.conf`, reinstalls the Hub service, and writes the new `.conf` file. Copy it to the new Travel machine and run Travel setup.

### WireGuard Keys

All keys are generated on Hub using `wg genkey` / `wg pubkey`. Keys have no machine binding — generating them centrally is safe and eliminates cross-machine coordination. Travel `.conf` files live in `C:\BTR\Extensibility\ConduentResource\` permanently (convenient for reinstalls). Each file contains a private key, so treat that directory accordingly.

---

## Resource Monitor

`ConduentResourceMonitor.exe` runs as a system tray application on `Hub` and `Travel` machines. It shows a **green circle** when all checks pass and **red** when any fail. Hover text shows per-item status. A Windows notification fires on each new failure.

### What Each Mode Monitors

| Check | Hub | Travel |
|---|---|---|
| pproxy / VPN | HTTP to internal Conduent URL via `conduent-resource:8888` | same |
| Port Forward | TCP connect to `localhost:8888` and `localhost:13389` | TCP connect to `conduent-resource:13389` |
| PAC Server | — | HTTP to `localhost:8080/conduent-resource.pac` |
| WireGuard | WireGuard tunnel service running | same |

Both modes start and own the Python PAC server (`python -m http.server`) on launch, killing it cleanly on exit.

### Right-Click Actions

Fix actions appear only when the corresponding check is failing:

- **Fix: Resource pproxy** — Hub only. Shows a reminder to RDP to the Resource machine, verify VPN is connected, and confirm the "Conduent-Resource - Resource Provider" terminal profile is running.
- **Fix: Repair Port Forwarding** — Hub only. Runs an embedded repair script elevated (UAC). Stops/starts the IP Helper service and re-applies `netsh portproxy` rules.
- **Fix: Restart WireGuard** — Restarts the WireGuard tunnel service elevated (UAC).
- **Fix: Restart PAC Server** — Travel only. Kills and restarts the Python PAC server process.

### Settings

Right-click → **Settings** to change any option persistently. Settings are saved to `ResourceMonitor.Hub.settings.json` or `ResourceMonitor.Travel.settings.json` next to the exe. Command line args override at runtime without writing back.

### Command Line Options

**Monitor:**

| Option | Description |
|---|---|
| `--mode Hub\|Travel` | Select mode. If omitted, inferred from settings file when exactly one exists. |
| `--repair-on-start` | Hub only. Run port proxy repair immediately on launch (use in startup shortcut). |
| `--check-url` | URL for VPN/pproxy health check. Default: `https://hrspwebtools001.americas.oneacs.com/msl` |
| `--tunnel-name` | WireGuard tunnel/service name. Default: `Hub-Tunnel` or `Travel-Tunnel` |
| `--pac-dir` | Directory containing `conduent-resource.pac`. Default: `C:\BTR\Extensibility\ConduentResource` |
| `--pac-port` | PAC HTTP server port. Default: `8080` |
| `--check-interval` | Seconds between checks. Default: `30` |
| `--notify-timeout` | Notification display time in ms. Default: `5000` |
| `--show-log` | Open the log window on startup. |

**Setup:**

| Option | Description |
|---|---|
| `--setup Hub\|Travel\|Resource` | Run guided setup wizard. |
| `--add-travel-config` | Add a new Travel machine to an existing Hub configuration. |
| `--conf-dir <path>` | Directory for WireGuard `.conf` files. Default: `C:\BTR\Extensibility\ConduentResource` |
| `--conf-file <path>` | Travel setup: path to the `.conf` file generated on Hub. |

---

See [Setup/readme.md](Setup/readme.md) for the full manual setup reference and details on what each wizard step does.
