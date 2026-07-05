# Conduent Resource Monitor

## What This Does

Conduent issues a laptop (`Resource`) with corporate VPN access. `Hub` is an always-on personal machine on the home LAN. `Travel` machines are personal laptops or devices used off the home LAN (hotels, coffee shops, etc.).

This setup lets Hub and Travel machines reach corporate resources — internal URLs, TFS, tools — as if sitting on the corporate network:

- `Resource` runs the tray monitor itself (`--mode Resource`), which natively exposes the corporate VPN as an HTTP proxy on port `8888` — no Python or third-party proxy tool involved.
- `Resource` can optionally bridge two OneDrive accounts — see [OneDrive Bridge Folder Sync](#onedrive-bridge-folder-sync-resource-only) below — since it's the only machine with local access to both.
- **WireGuard** creates an encrypted tunnel between `Hub` and each `Travel` machine. Each peer's `AllowedIPs` is scoped to a single `/32` address, so `Travel` can reach `Hub` itself (e.g. RDP straight to Hub) from anywhere — but not other devices on Hub's home LAN.
- **Port forwarding** on `Hub` routes proxy and RDP traffic arriving over the WireGuard tunnel onward to `Resource`, since `Resource` is a separate device on Hub's LAN and isn't itself a WireGuard peer.
- **PAC file** on `Hub` and `Travel` machines tells browsers which URLs to route through the proxy.

### Traffic Paths

| From | To | What | How |
|---|---|---|---|
| `Hub` | `Resource` | VPN proxy (`:8888`) | Direct — same home LAN. `Hub`'s hosts file maps `conduent-resource` straight to Resource's LAN IP. |
| `Hub` | `Resource` | RDP (`:3389`) | Direct — same home LAN. Kept open manually to keep Global Connect VPN alive (see below). |
| `Travel` | `Hub` | RDP (`:3389`) | Direct — WireGuard makes `Travel` and `Hub` peers on the same virtual subnet. `Travel` RDPs straight to `Hub`'s tunnel IP (`10.0.0.1`); no forwarding involved. Requires Remote Desktop enabled on `Hub` — this repo doesn't configure that part. |
| `Travel` | `Resource` | VPN proxy (`:8888`) | Indirect. `Travel`'s hosts file maps `conduent-resource` to `Hub`'s tunnel IP (`10.0.0.1`), not Resource. `Hub` port-forwards `8888` → `Resource:8888`. |
| `Travel` | `Resource` | RDP (`:13389` → `:3389`) | Indirect, same path as above. `Hub` port-forwards `13389` → `Resource:3389`. |

`Resource` only exists on Hub's home LAN — it's never a WireGuard peer — which is why reaching it from `Travel` needs the port-forward step instead of a direct tunnel hop.

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
The wizard collects Hub's static LAN IP (assigned during router setup above), then creates a firewall rule scoped to that IP and a startup shortcut. No Python or third-party proxy tool is installed — the proxy is the tray monitor itself, running as `--mode Resource`.

**2. Hub** (on the always-on home machine)
```
ConduentResourceMonitor.exe --setup Hub
```
Collects Resource's static IP, Hub's public IP (auto-fetched), Travel machine names, and config directory — each on the wizard step that uses it. Generates all WireGuard keys centrally — no manual key exchange needed. Installs Hub tunnel service, firewall rules, port proxy rules, hosts file, PAC file, and startup shortcut.

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

`ConduentResourceMonitor.exe` runs as a system tray application on `Hub`, `Travel`, and `Resource` machines. It shows a **green circle** when all checks pass and **red** when any fail. When [folder sync](#onedrive-bridge-folder-sync-resource-only) is configured and active, the icon becomes **sync arrows** instead of a plain circle — still green/red based on the monitored checks only, since sync errors never change icon color. Hover text shows per-item status. A Windows notification fires on each new failure.

### What Each Mode Monitors

| Check | Hub | Travel | Resource |
|---|---|---|---|
| `Resource VPN` (Hub/Travel) / `VPN Enabled` (Resource) | HTTP to internal Conduent URL via `conduent-resource:8888` | same | HTTP to internal Conduent URL via `localhost:8888` |
| Port Proxy / Forwarding | TCP connect to `localhost:8888` and `localhost:13389` | — | — |
| Resource RDP | — | TCP connect to `conduent-resource:13389` | — |
| VPN Proxy | — | — | TCP connect to `localhost:8888` |
| PAC Web Server | HTTP to `localhost:8080/conduent-resource.pac` | same | — |
| WireGuard | WireGuard tunnel service running | same | — |

Resource is never a WireGuard peer and never serves a PAC file (no PAC consumers on that box), so those two checks don't apply there.

Hub and Travel start and own a native PAC file server (an in-process TCP listener, no external process) on launch, killing it cleanly on exit. Resource starts and owns the native proxy listener the same way. Neither depends on Python.

### Auto-Repair

When a check fails, the monitor automatically attempts repairs that do not require elevation, up to 2 times. Each attempt is logged. Once the check passes, the attempt counter resets. If both attempts fail, the fix action remains available in the right-click menu for manual use. Resource auto-repairs its own native proxy listener the same way Hub/Travel auto-repair the PAC server — but VPN reconnection stays manual everywhere, since it requires physically reconnecting Global Connect.

### Right-Click Actions

Fix actions appear only when the corresponding check is failing:

- **Fix: Check Resource VPN** — Hub/Travel only. Reminds you to remote into the Resource machine and ensure VPN is currently running and enabled.
- **Fix: Enable VPN** — Resource only. Reminds you to log into VPN on this machine directly, no remoting involved.
- **Fix: Repair Port Proxy Rules** — Hub only. Runs an embedded repair script elevated (UAC). Stops/starts the IP Helper service and re-applies `netsh portproxy` rules.
- **Fix: Restart WireGuard** — Restarts the WireGuard tunnel service elevated (UAC). Hub/Travel only.
- **Fix: Restart PAC Web Server** — Restarts the native PAC file listener. Hub/Travel only.
- **Fix: Restart VPN Proxy** — Restarts the native proxy listener. Resource only.

When [folder sync](#onedrive-bridge-folder-sync-resource-only) is configured, two more items appear (not tied to a failing check): **Pause Syncing** / **Resume Syncing**, and **Purge Sync Trash (N files)**.

### Settings

Right-click → **Settings** to change any option persistently. Settings are saved to a single `ResourceMonitor.settings.json` next to the exe (the `Mode` field inside it tracks Hub vs Travel vs Resource — there's no separate file per mode). Command line args override at runtime without writing back. Tunnel Name / PAC Directory / PAC Port are hidden when Resource is selected, since Resource uses neither WireGuard nor PAC serving; Hub Sync Path / Resource Sync Path / Sync Ignore Patterns only show for Resource.

### Command Line Options

**Monitor:**

| Option | Description |
|---|---|
| `--mode Hub\|Travel\|Resource` | Select mode. If omitted, inferred from settings file when exactly one exists. |
| `--repair-on-start` | Hub only. Run port proxy repair immediately on launch (use in startup shortcut). |
| `--check-url` | URL for VPN Proxy health check. Default: `https://hrspwebtools001.americas.oneacs.com/msl` |
| `--tunnel-name` | WireGuard tunnel/service name. Hub/Travel only. Default: `Hub-Tunnel` or `Travel-Tunnel` |
| `--pac-dir` | Directory containing `conduent-resource.pac`. Hub/Travel only. Default: `C:\BTR\Extensibility\ConduentResource` |
| `--pac-port` | PAC HTTP server port. Hub/Travel only. Default: `8080` |
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

## OneDrive Bridge Folder Sync (Resource only)

`Resource` is the only machine with local access to both the Hub account's and the Resource account's OneDrive. Setting `HubSyncPath` and `ResourceSyncPath` (both required together — blank either to turn the feature off) mirrors matching top-level folders between the two local OneDrive roots, letting the OneDrive clients handle the actual cloud transport.

- **Which folders sync**: top-level directories under `HubSyncPath` define the sync set; same-named directories under `ResourceSyncPath` mirror them recursively. Other content at the Resource root is ignored.
- **Automatic pinning + hydration wait**: the app pins each matching top-level folder itself (recursively, the instant it becomes managed) — no Explorer action required, ever. Pinning is instant (metadata only); actually reading/copying a folder's content waits until it's fully hydrated (no cloud-only placeholders left, on both sides), so the engine never triggers a synchronous OneDrive download mid-reconcile. A folder still downloading is skipped for content sync only (existence/orphan checks are unaffected), shown in the tray as "waiting on OneDrive", with one balloon per app launch — it self-heals into normal sync as soon as hydration finishes, no restart needed.
- **Deletes are safe**: a file deleted on one side is soft-deleted — the other side's copy moves to `SyncTrash\` (app directory) instead of being erased. Right-click → **Purge Sync Trash** to empty it once you're sure. Deleting an entire top-level folder from the **Hub** side is treated as an orphan (Resource's copy is left alone, sync just stops for that folder); deleting a top-level folder from the **Resource** side is an ordinary delete (Hub's copies go to SyncTrash).
- **Conflicts** (both sides edited a file since last sync): the newer file wins; the older version is preserved in `SyncConflict\` (app directory) rather than lost.
- **Activity log**: `sync.log` next to the exe (also shown in the log window).
- **Pause/Resume**: via the tray right-click menu; persists across restarts.

Setup Resource wizard step: [Setup/readme.md](Setup/readme.md#resource-steps) collects both paths and a semicolon-delimited ignore-pattern list (default `~$*;*.tmp;desktop.ini;Thumbs.db`) — pinning and hydration-waiting happen automatically once the monitor is running, so the step doesn't check either.

---

See [Setup/readme.md](Setup/readme.md) for the full manual setup reference and details on what each wizard step does.
