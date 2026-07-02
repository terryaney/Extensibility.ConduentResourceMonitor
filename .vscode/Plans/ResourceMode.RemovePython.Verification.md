# Verification: Native `AppMode.Resource` — Remove Python

Two-phase manual verification for the changes in [ResourceMode.RemovePython.md](ResourceMode.RemovePython.md).

**Hub and Travel need zero changes for either phase.** Their checks (`ProxyCheck`,
`PortForwardCheck`, `WireGuardCheck`) just do a TCP connect / HTTP GET against the same
addresses as before — they have no visibility into whether Resource is running pproxy or
the native listener. Leave Hub/Travel on their current build for this verification.

---

## Phase 1 — Drop-in replacement, no setup, no firewall changes

Goal: confirm the native proxy is a functional swap for pproxy using the *existing*,
unscoped firewall rule — no `--setup Resource`, no firewall edits.

**On Resource:**

1. Confirm what's currently holding port 8888, then close it (close the pproxy Windows
   Terminal tab):
   ```powershell
   Get-NetTCPConnection -LocalPort 8888 -State Listen | Select-Object OwningProcess
   Get-Process -Id <pid>   # should show pwsh/python
   ```
2. Confirm the port is actually free afterward — closing a terminal tab doesn't always kill
   an orphaned child process:
   ```powershell
   Get-NetTCPConnection -LocalPort 8888 -State Listen
   ```
   **Expected:** `Get-NetTCPConnection: No matching MSFT_NetTCPConnection objects found...`
   — this is the correct, expected result. It means nothing is listening on 8888, i.e. the
   port is free. This is *not* an error to troubleshoot.
3. Copy the new build into the existing `C:\BTR\Extensibility\ConduentResource\`
   (overwrite `ConduentResourceMonitor.exe`).
4. Launch it explicitly in Resource mode (no `settings.json` exists there yet, so pass
   `--mode` to skip the mode-picker dialog):
   ```powershell
   C:\BTR\Extensibility\ConduentResource\ConduentResourceMonitor.exe --mode Resource --show-log
   ```
5. Confirm: tray icon appears and goes **green**; the log window shows both `VPN Enabled`
   and `VPN Proxy` as `OK` within one check cycle (30s default).
6. Confirm it's really *your* exe on the port now, not a leftover process:
   ```powershell
   Get-NetTCPConnection -LocalPort 8888 -State Listen | Select-Object OwningProcess
   Get-Process -Id <pid>   # should be ConduentResourceMonitor
   ```

**From Hub, then from Travel:** browse your vpn asset through the existing proxy config
exactly as you do today. No config changes needed on either — they're still pointed at
`conduent-resource:8888` (Hub direct, Travel via WireGuard→Hub→portproxy), and that now
lands on the native listener instead of pproxy. Confirm it loads.

If this works, Phase 1 is done — the native proxy is a functional drop-in replacement with
the old, unscoped firewall rule still in place.

---

## Phase 2 — Run setup, apply the scoped firewall rule

Goal: confirm `--setup Resource` produces a firewall rule scoped to `profile=private` +
`remoteip=<Hub's LAN IP>`, and that Hub/Travel connectivity survives the tightening.

**On Resource:**

1. Close the Phase 1 instance (or leave it running — the firewall change applies live
   either way, but a clean relaunch afterward makes verification unambiguous).
2. Delete any old rule first — either the pre-migration unscoped `pproxy` rule (if you never
   cleaned it up after Phase 1) or a stale `CRM - VPN Proxy` rule from an earlier test run.
   This matters because `VpnProxyFirewallStep.IsAlreadyCompleteAsync()` only checks that a
   rule *named* `CRM - VPN Proxy` exists, not its scope — if a same-named rule is already
   there, the wizard will see "already complete" and skip re-creating it, silently leaving
   whatever scope it already had:
   ```powershell
   netsh advfirewall firewall delete rule name=pproxy
   netsh advfirewall firewall delete rule name="CRM - VPN Proxy"
   ```
3. Run setup:
   ```powershell
   C:\BTR\Extensibility\ConduentResource\ConduentResourceMonitor.exe --setup Resource
   ```
   A preflight dialog now appears asking for **Hub's Static LAN IP** — enter it (the static
   lease you already assigned during router setup). Approve the UAC prompt for the firewall
   step.
4. Verify the rule directly:
   ```powershell
   netsh advfirewall firewall show rule name="CRM - VPN Proxy" verbose
   ```
   Confirm `Profiles: Private` and `RemoteIP: <Hub's IP>` (not `Any`). Also confirm the old
   `pproxy`-named rule is gone (the wizard only ever manages `CRM - VPN Proxy` now, so a
   leftover `pproxy` rule from Phase 1 won't get cleaned up automatically):
   ```powershell
   netsh advfirewall firewall show rule name=pproxy
   ```
   **Expected:** "No rules match the specified criteria." — confirms nothing unscoped is
   still open on port 8888.
5. **Cleanup check:** if you had an old startup shortcut for the pwsh/pproxy script
   (`Conduent-Resource - Resource Provider.lnk` in `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`),
   delete it manually — the wizard creates a *new*, differently-named shortcut
   (`Conduent Resource Monitor - Resource.lnk`) and won't touch the old one, so both would
   try to run on next reboot otherwise.
6. Relaunch (or use the new startup shortcut) and confirm the tray icon is still green.

**From Hub:** browse the vpn asset again — should still work (Hub's IP is exactly what's
now allow-listed).

**From Travel:** browse the vpn asset again — should still work (Travel's traffic still
arrives at Resource *as* Hub's IP, via the portproxy forward — this was the whole basis for
scoping to Hub's IP alone).

**Negative test (optional, needs a second LAN device that isn't Hub):** from any other
machine on your home network, attempt:
```powershell
curl -x http://<Resource-LAN-IP>:8888 https://example.com
```
This should fail to connect — confirms the scoping actually blocks non-Hub sources rather
than just looking configured.
