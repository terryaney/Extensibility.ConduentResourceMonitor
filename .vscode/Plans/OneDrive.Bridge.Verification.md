# Phase 0 — before anything

Check Task Manager for a running `ConduentResourceMonitor.exe` and exit it (right-click tray icon → Exit, or kill it in Task Manager) if found. Reason: both the automated script and any live instance write to the **same** files next to the exe — `ResourceMonitor.settings.json`, `sync.ledger.json`, `sync.log`, `SyncTrash\`, `SyncConflict\` — regardless of which sync paths each is pointed at. Two processes touching those simultaneously will corrupt/race each other.

# Phase 1 — automated script (run this first)

```
cd C:\BTR\Extensibility\ConduentResource
.\Verify-FolderSync.ps1 -ExeDir "C:\BTR\Extensibility\ConduentResource"
```

You **must** pass `-ExeDir` explicitly — the script's default (`bin\Debug\net10.0-windows`) assumes a dev build layout, not this deployed folder.

Let it run to completion (don't Ctrl+C) — it backs up your real `ResourceMonitor.settings.json` at the start and restores it in a `finally` block at the end, so your real config is untouched either way, but only if it's allowed to finish. Confirm you see `31/31 assertions passed.`

# Phase 2 — configure the real sync paths

1. Create the Hub root if it doesn't exist yet — it's a plain folder, not auto-created by setup:

```
mkdir "C:\Users\20813678\OneDrive\MonitorTest"
```

(`OneDrive - Conduent` already exists as the Resource account's own root — nothing to create there.)
2. Since Resource is presumably already fully set up otherwise (`setup.log` shows it's been run), the simplest path is: launch the monitor, right-click the tray icon → **Settings**, fill in **Hub Sync Path** = `C:\Users\20813678\OneDrive\MonitorTest`, **Resource Sync Path** = `C:\Users\20813678\OneDrive - Conduent`, leave Sync Ignore Patterns at default, click OK. That writes settings and restarts sync immediately — no need to re-run the full wizard. (`--setup Resource` also works and will show the other steps already complete, if you prefer that route.)

# Phase 3 — manual verification against real OneDrive

Prooves the real cloud round-trip, not just the local engine logic.

Machines referenced below: **Hub** = your personal dev machine (this one), **Resource** = the
Conduent laptop running the monitor.

## Steps

**Steps 7–12 aren't strictly sequential in real time.** Hydration completion (whether
`TestHydrating` clears `HYDRATING` and copies) happens on OneDrive's own download timeline, not on
whichever step number you're currently reading. For a small file it can resolve within seconds of
resuming — before you've even finished reading step 7's log output. For a genuinely large folder
it might still be downloading by the time you reach step 12. Either is fine; just check the actual
timestamps in `sync.log` rather than assuming it lines up with your position in this checklist.

1. **[Resource]** Right-click the tray icon → **Pause Syncing**.
   **Verify:** tooltip reads `Sync: Paused`.

   Why pause first: a new top-level folder fires `_fullPending` and the engine runs a full
   reconcile *immediately* — no debounce window to beat. Doing all folder setup while paused
   avoids a race where the app auto-pins `TestPrepinned` before you get to Explorer.

2. **[Resource]** Under `C:\Users\20813678\OneDrive\MonitorTest`, create `TestPrepinned`, then
   right-click it → **Always keep on this device**.
   **Verify:** green "always available" check appears on the folder in Explorer.
   (Pinning must happen on Resource — it's a per-device attribute, OneDrive doesn't replicate it
   from Hub.)

3. **[Hub]** Go to your own local `OneDrive\MonitorTest` (same account as Resource's `HubSyncPath`
   — create the folder if it hasn't synced down to this machine yet). Create `TestNormal` and add
   a small file, e.g. `hello.txt` with some text.
   **Verify:** file exists locally on Hub.

4. **[Hub]** Also under `OneDrive\MonitorTest`, create `TestHydrating` and copy in a real,
   sizeable (100MB+) folder of content.
   **Verify:** OneDrive's taskbar icon shows upload activity for it.

5. **[Hub → cloud → Resource]** Wait for OneDrive to sync `TestNormal` and `TestHydrating` down to
   Resource's local `HubSyncPath`. This is real cloud latency — seconds to a couple minutes, not
   instant like the Phase 1 script.
   **Verify:** on Resource, `MonitorTest\TestNormal\hello.txt` and `MonitorTest\TestHydrating\...`
   appear (possibly as cloud-only/placeholder icons — expected, that's the hydration state we want
   to test next).

6. **[Resource]** Right-click tray → **Resume Syncing**.
   **Verify:** tooltip briefly shows the spinning-arrows icon (a reconcile pass running), then
   settles.

7. **[Resource]** Open **Show Log** (or tail `sync.log`) and watch for, roughly in this order:
   - `PIN 'TestNormal' (Hub) — newly managed folder pinned.` (the coarse, recursive "this whole
     top-level folder just became managed" pin, Hub side)
   - `PIN 'TestNormal' (Resource) — new folder pinned.` — note the different wording from the
     line above: this one fires from inside `CopyAcross` the moment the mirror folder is created
     to receive the first file, not from the same top-level "newly managed" check (which never
     gets a chance to log Resource, since `CopyAcross` always wins the race — see the note below).
   - `COPY 'TestNormal\hello.txt' Hub→Resource`
   - `PIN 'TestHydrating' (Hub) — newly managed folder pinned.`
   - `HYDRATING 'TestHydrating' — still downloading from OneDrive; content sync skipped this pass.`
     repeated across passes for as long as the content is still downloading — could be one
     occurrence or many, entirely depending on file size and OneDrive's own speed.
   - Once OneDrive actually finishes downloading it (seconds to minutes later, unrelated to which
     step you're on): `PIN 'TestHydrating' (Resource) — new folder pinned.` followed by
     `COPY 'TestHydrating\...' Hub→Resource` — no restart required, this just shows up in the log
     on its own next pass.
   - **No** `PIN` line at all for `TestPrepinned` — confirms the engine correctly leaves an
     already-pinned folder alone.

   **Verify:** tray tooltip shows `Sync: waiting on OneDrive (1 folders)` while `TestHydrating` is
   still pending (it may already have cleared by the time you check — that's fine, see the note
   above the steps); Explorer shows the green pinned check on `TestNormal` and `TestPrepinned` on
   **both** sides (`OneDrive\MonitorTest\...` and `OneDrive - Conduent\MonitorTest\...`).

8. **[Resource]** Confirm exactly one balloon notification: "Sync Waiting" mentioning
   `TestHydrating`.

9. **[Resource]** Drop a file directly into `C:\Users\20813678\OneDrive\MonitorTest\TestPrepinned`
   (the Hub-side path, on the Resource machine — not the Conduent side, which doesn't exist yet.
   Simplest to do this one locally on Resource rather than round-tripping through Hub, since its
   purpose is just proving an already-pinned folder syncs normally, not testing propagation).
   **Verify:** the mirror folder `OneDrive - Conduent\MonitorTest\TestPrepinned` gets *created*
   (it doesn't exist until the first file lands, since mirror folders are only created lazily as a
   byproduct of copying a file into them — an empty top-level folder never gets an empty mirror)
   and the file appears in it within the next targeted reconcile (~3s debounce); `sync.log` shows a
   `COPY` line with no `PIN` line for this folder.

10. **[Resource]** Confirm `TestNormal`'s file mirrored into
    `OneDrive - Conduent\MonitorTest\TestNormal\hello.txt`.
    **Verify:** content matches; both sides show the pinned checkmark.

11. **[Resource → cloud → VDI]** *(only if you have a VDI session signed into the Conduent OneDrive
    account)* Wait for `OneDrive - Conduent\MonitorTest\...` to cloud-sync up and back down to VDI.
    **Verify:** the files appear inside the VDI session's `OneDrive - Conduent` folder — this
    closes the full Hub → Resource → VDI loop end-to-end.

12. **[Resource]** Check back on `TestHydrating`: has it already cleared (per step 7's note, it may
    have finished minutes ago)? If `sync.log` is still showing `HYDRATING` lines for it, OneDrive is
    still downloading a larger folder — either wait it out (watch its taskbar progress), or force an
    earlier recheck instead of waiting for the 10-minute timer by editing any file inside it.
    **Verify:** `HYDRATING` lines stop appearing, the tray's "waiting on OneDrive" count drops to 0,
    and a normal `COPY` line shows up for its contents — no restart required, whenever it clears.

13. **[Resource]** Wrap-up check: tray tooltip settles to `Sync: OK (last HH:mm)`, 0 pending
    hydration, 0 errors.

## Cleanup

Test folders under `MonitorTest` are real content in your real OneDrive accounts — delete them
afterward if you don't want them hanging around, or leave them as a permanent smoke-test pair.

## Reset (start the flow over)

Unlike Phase 1's synthetic script, this is real OneDrive content, so "reset" means actually
deleting real folders, not wiping a throwaway temp directory. You do **not** need to touch
`sync.ledger.json` for this — deleting a top-level folder from the Hub side is exactly what the
engine's existing orphan rule is built to detect and clean up on its own.

1. **[Resource]** Right-click tray → **Pause Syncing** (avoids the engine reacting mid-delete).
2. **[Resource]** Delete the test folders from both sides, wherever they currently exist:
   - `C:\Users\20813678\OneDrive\MonitorTest\{TestNormal, TestHydrating, TestPrepinned}`
   - `C:\Users\20813678\OneDrive - Conduent\MonitorTest\{TestNormal, TestHydrating, TestPrepinned}`
3. **[Hub]** Delete the matching folders under your own local `OneDrive\MonitorTest` too, so
   nothing re-propagates back down to Resource from the cloud a few minutes later.
4. **[Resource]** Right-click tray → **Resume Syncing**.
   **Verify:** `sync.log` shows an `ORPHAN '<name>' removed from Hub root...` line for each
   deleted folder, and the ledger's stale entries for them are dropped automatically — no manual
   ledger editing needed.
5. Recreate the test folders fresh, starting again from step 2 above.

**One limitation:** once a folder is pinned, its content stays fully downloaded — there's no way
to force it back to a cloud-only placeholder short of unpinning and using Explorer's "Free up
space," which fights the whole point of pinning. So to re-test the **hydration gate** specifically,
don't try to reuse the same `TestHydrating` folder — delete it and drop a *newly*-created large
file/folder on the Hub side instead, so it arrives at Resource as a genuine not-yet-hydrated
placeholder again.

**If you want a truly from-scratch run** (not just these test folders, e.g. the ledger has gotten
confusing for unrelated reasons): delete `sync.ledger.json` next to the exe entirely. The next
reconcile treats everything as a first run (additive union merge, no deletes) — heavier-handed
than needed for just re-running this test, but useful if state ever looks wrong in a way you can't
otherwise explain.
