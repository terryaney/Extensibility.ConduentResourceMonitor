# Stop failure-balloon spam (offline gate + grouped/debounced notifications)

## Context

On Travel, launching with no wifi/internet yet available causes every configured check
(Resource VPN, Port Proxy/Forwarding, PAC Web Server, WireGuard Service) to fail, and the
user gets "pounded over and over" with individual toast balloons — not just once at
startup, but repeatedly, because today's notify logic in
[Services/MonitorService.cs:34-50](../../Services/MonitorService.cs#L34-L50)
fires a fresh balloon for a check every single time it edge-transitions from OK to FAIL
(`wasOk != false`) — and with flaky/captive-portal wifi, or a check that flickers, that
edge re-triggers repeatedly. On top of that, several checks failing at once (all from one
root cause) each fire their own independent balloon via
[TrayApp.cs:272-282](../../TrayApp.cs#L272-L282)
instead of being reported as one combined issue.

Two changes fix this, agreed with the user:
1. **Offline gate**: skip notifying entirely when a quick connectivity probe shows the
   machine has no internet at all (the tray icon already goes red and the tooltip already
   lists FAIL lines independent of any balloon — see `UpdateTrayPresentation`,
   [TrayApp.cs:244-262](../../TrayApp.cs#L244-L262)
   — so no other UI change is needed there).
2. **Grouped + debounced notifications**: when multiple checks fail in the same pass,
   send **one** balloon listing all of them ("Multiple Issues: A, B, C") instead of N
   separate balloons. And once a given check has been notified about, don't re-notify
   about that same check again for **15 minutes**, even if it flaps fail/ok/fail in the
   meantime — a genuine recovery (one full pass reporting OK) resets that cooldown so a
   later failure notifies immediately, as today.

No existing connectivity-check utility exists in the codebase (confirmed via search).

## Approach

### 1. New connectivity probe: `Services/InternetConnectivityCheck.cs`

Static helper, following the exact `TcpClient.ConnectAsync(...).WaitAsync(timeout)`
pattern already used by
[Checks/PortForwardCheck.cs:16-17](../../Checks/PortForwardCheck.cs#L16-L17).
Probes two well-known public IPs directly on port 443 (no DNS lookup — DNS itself can be
down when offline), in parallel, returns `true` if either succeeds.

Hardcoded 2-second timeout, no new `AppSettings` field — internal gate, not a user-facing
monitored check.

### 2. Rework notify logic in `Services/MonitorService.cs`

Replace the edge-triggered `_lastState`/`CheckFailed` mechanism with a cooldown-based,
batched one:
- Drop `_lastState` and the `CheckFailed` event.
- Add `_lastNotifiedAt` (`Dictionary<string, DateTime>`) and a `NotifyCooldown` constant
  (15 minutes).
- Add a new event `ChecksFailed` that fires **once per pass** with the full list of checks
  newly eligible to notify about (empty list never fires).
- A check reporting `Ok` clears its cooldown entry (so the next failure after a genuine
  recovery notifies immediately, matching prior behavior).
- A check still failing is "eligible" if it's never been notified, or its last
  notification was ≥ 15 minutes ago.
- Only probe connectivity when there's at least one eligible check (skip the probe
  entirely on healthy passes). If offline, skip firing this pass — eligible checks stay
  eligible (not marked notified) so they're retried, and re-probed, next tick.

### 3. Update `TrayApp.cs` to consume the grouped event

- `_monitor.CheckFailed += OnCheckFailed;` → `_monitor.ChecksFailed += OnChecksFailed;`
- `OnCheckFailed` replaced with `OnChecksFailed( IReadOnlyList<CheckResult> results )`:
  single-result case keeps the original wording ("{Name} Failed" / "{Name} is no longer
  connected..."); multi-result case sends one "Multiple Issues" balloon listing all names.

### Scope notes
- Sync-related balloons (`OnSyncStatus`, [TrayApp.cs:203-238](../../TrayApp.cs#L203-L238))
  are untouched — not reported as spamming, and already have their own one-shot/re-arm
  logic.
- Icon color and tooltip text behavior are unchanged — already reflect FAIL state
  independent of whether a balloon fired.
- Auto-repair logic in `OnResultsUpdated` ([TrayApp.cs:163-180](../../TrayApp.cs#L163-L180))
  is untouched; it's independent of the notify path.

## Status: Implemented

- `Services/InternetConnectivityCheck.cs` added.
- `Services/MonitorService.cs`: `_lastState`/`CheckFailed` replaced with
  `_lastNotifiedAt`/`ChecksFailed` + cooldown + offline gate, per above.
- `TrayApp.cs`: wired to `ChecksFailed`, `OnCheckFailed` replaced with grouped
  `OnChecksFailed`.
- `dotnet build` verified: 0 errors, 0 warnings.

## Verification (manual, not yet performed)
- Disconnect wifi/network, launch the app (or use `--mode Travel`), confirm the tray icon
  goes red and the tooltip lists FAIL lines, but no balloons pop — including on subsequent
  30s ticks while still offline.
- Reconnect wifi while a real failure persists (e.g. stop the WireGuard service before
  reconnecting), confirm one balloon fires for the still-failing check once connectivity
  is back.
- With wifi on, fail two checks at once (e.g. stop WireGuard service and block the PAC
  port), confirm a single "Multiple Issues" balloon listing both, not two balloons.
- With wifi on, force a single check to flap fail/ok/fail within a couple of minutes (or
  just let one genuinely stay down), confirm it only balloons once, not every 30s pass,
  until 15 minutes elapse or it genuinely recovers first.
