# ResourceMonitor Implementation

## What This Is

`ConduentResourceMonitor.exe` replaces the old `ResourceMonitor.ps1` + `ResourceMonitor.bat` + `WireguardPortproxy.bat` combination. It is a .NET 10 WinForms system tray application that monitors the Conduent VPN tunnel infrastructure on `Hub` and `Travel` machines.

---

## Project

- **Location**: `C:\BTR\Extensibility\ConduentResourceMonitor\`
- **Framework**: `net10.0-windows`, `OutputType: WinExe` (no console window)
- **NuGet**: `CommandLineParser 2.9.1`

### Build

```
dotnet build
```

### Publish (single exe — requires .NET 10 on target)

```
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true
```

Output: `bin\Release\net10.0-windows\win-x64\publish\ConduentResourceMonitor.exe`  
Deploy only that one file. The settings JSON files are created at runtime next to the exe on first Settings save.

---

## Source Files

```
ConduentResourceMonitor.csproj
Program.cs              — entry point, mode inference, validation, Application.Run
Options.cs              — CommandLineParser options (all flags)
AppSettings.cs          — settings model, Load/Save (per-mode JSON), Validate(), InferMode()
TrayApp.cs              — ApplicationContext subclass, tray icon, context menu, notifications
LogForm.cs              — scrollable log window (hide on close, max 1000 lines)
SettingsForm.cs         — WinForms dialog for persistent settings

Checks\
  CheckResult.cs        — record(Name, Ok, Detail)
  ICheck.cs
  ProxyCheck.cs         — HTTP via conduent-resource:8888 proxy (pproxy on Hub, VPN on Travel)
  PortForwardCheck.cs   — TCP connect check (Hub: localhost:8888+13389, Travel: conduent-resource:13389)
  WireGuardCheck.cs     — runs `wg show`, checks for tunnel name in output
  PacServerCheck.cs     — HTTP GET localhost:<port>/conduent-resource.pac

Repairs\
  IRepair.cs
  PortProxyRepair.cs    — writes embedded bat to %TEMP%, runs elevated (UAC); startupDelay=true adds 60s wait
  WireGuardRepair.cs    — sc stop/start WireGuardTunnel$<name> via elevated cmd
  PacServerRepair.cs    — kills and restarts PacServerService

Services\
  PacServerService.cs   — starts/stops python -m http.server
  MonitorService.cs     — WinForms Timer loop, fires ResultsUpdated + CheckFailed events
```

---

## Modes

### Hub (3 checks)
| Check | What |
|---|---|
| pproxy | HTTP GET CheckUrl via `http://conduent-resource:8888` |
| PortFwd | TCP connect to `localhost:8888` and `localhost:13389` |
| WireGuard | `wg show` contains `interface: Hub-Tunnel` |

### Travel (4 checks)
| Check | What |
|---|---|
| VPN | HTTP GET CheckUrl via `http://conduent-resource:8888` |
| PortFwd | TCP connect to `conduent-resource:13389` |
| PAC | HTTP GET `http://localhost:8080/conduent-resource.pac` |
| WireGuard | `wg show` contains `interface: Travel-Tunnel` |

Both modes start and own a `python -m http.server` process (PAC server). It is killed on exit.

---

## Key Behaviors

- **Icon**: Green = all OK, Red = any failing. Built at runtime via `System.Drawing`.
- **Hover text**: `pproxy: OK | PortFwd: FAIL | WireGuard: OK` (63-char Windows limit, truncated if needed).
- **Notifications**: `ShowBalloonTip` on transition to FAIL per check item. One notification per item per transition.
- **Fix actions**: Appear in right-click menu only when the corresponding check is failing. Removed when resolved.
- **Settings**: Persisted to `ResourceMonitor.<Mode>.settings.json` next to exe. CLI args override at runtime without writing back.
- **`--repair-on-start`**: Hub only. Fires `PortProxyRepair.Execute(startupDelay: true)` immediately on launch — writes embedded bat to `%TEMP%`, runs elevated, includes 60s initial delay for boot network timing, self-deletes when done.
- **Mode inference**: If `--mode` is omitted and exactly one `ResourceMonitor.<Mode>.settings.json` exists, that mode is used automatically.
- **Validation**: All settings checked before starting. Clear error dialog lists problems if anything is missing or invalid.

---

## Settings File Defaults

`ResourceMonitor.Hub.settings.json`:
```json
{
  "CheckUrl": "https://hrspwebtools001.americas.oneacs.com/msl",
  "ProxyAddress": "conduent-resource:8888",
  "PacPort": 8080,
  "PacDirectory": "C:\\BTR\\Extensibility\\ConduentResource",
  "TunnelName": "Hub-Tunnel",
  "CheckIntervalSeconds": 30,
  "NotifyTimeoutMs": 5000
}
```

`ResourceMonitor.Travel.settings.json` — same but `TunnelName: "Travel-Tunnel"`.

---

## Startup Shortcuts

### Hub
```
ConduentResourceMonitor.exe --mode Hub --repair-on-start
```

### Travel
```
ConduentResourceMonitor.exe --mode Travel
```

See `readme.md` for PowerShell shortcut creation scripts.

---

## Verification Steps

### 1. Basic launch

```
ConduentResourceMonitor.exe --mode Travel --show-log
```
- Tray icon appears (green or red)
- Log window opens and shows check results after a few seconds
- Hover over icon — per-item status visible

### 2. PAC server ownership

- Launch in Travel mode
- Open Task Manager → find `python` process running `http.server 8080`
- Right-click tray → Exit
- Verify python process is gone

### 3. PAC server failure + repair

- Launch in Travel mode
- Kill the python process manually in Task Manager
- Wait for next check cycle (≤30s) — PAC item should go red, notification fires
- Right-click tray → `Fix: Restart PAC Server` appears
- Click it — PAC goes green on next cycle

### 4. Log window

- Right-click → Show Log
- Verify entries appear as `[HH:mm:ss] CheckName: OK/FAIL (detail)`
- Close the window — verify app is still running (icon still visible)
- Right-click → Show Log again — window reopens

### 5. Settings dialog

- Right-click → Settings
- Change `CheckIntervalSeconds` to `10`, click OK
- Verify checks now run every 10s (watch log)
- Change back to `30`, click OK

### 6. Clean exit

- Right-click → Exit
- Verify tray icon disappears immediately (no hover-to-dismiss)
- Verify python process terminated

### 7. Hub mode

```
ConduentResourceMonitor.exe --mode Hub --show-log
```
- Verify 3 checks run: `pproxy`, `PortFwd`, `WireGuard`
- If on a machine without WireGuard installed, WireGuard check goes red with a meaningful error

### 8. --repair-on-start (Hub, on actual Hub machine)

```
ConduentResourceMonitor.exe --mode Hub --repair-on-start
```
- UAC prompt appears immediately on launch
- After approving, a cmd window opens showing the repair script running
- 60s delay, then iphlpsvc restart, then portproxy rules re-applied
- Window shows `netstat` output confirming 8888 and 13389 listening, then closes

### 9. Mode inference

- Ensure only `ResourceMonitor.Travel.settings.json` exists next to the exe (no Hub file)
- Run `ConduentResourceMonitor.exe` with no arguments
- Verify Travel mode starts automatically

### 10. Validation error

```
ConduentResourceMonitor.exe --mode Hub --pac-dir C:\does\not\exist
```
- Error dialog appears listing: `PacDirectory does not exist: 'C:\does\not\exist'`
- App exits cleanly
