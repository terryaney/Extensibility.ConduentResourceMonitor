# OneDrive Bridge: Replace Root Pin-Gate with Auto-Pin + Per-Folder Hydration Gate

## Context

The Resource setup wizard's "Configure Sync Folders" step (and the runtime pin-gate in `FolderSyncService`) currently checks `PinHelper.IsPinned` on the **root** `HubSyncPath`/`ResourceSyncPath` the user configures. Live testing showed this is wrong: the user's `ResourceSyncPath` is `C:\Users\20813678\OneDrive - Conduent` (an entire OneDrive library root), and that folder itself never carries the pinned attribute even though its actual children (`_VDI`, `BTR Public`, `ErikDarling`) already show green "always available" checks in Explorer. The wizard was permanently blocked on a folder that was never going to report pinned.

Discussion converged on a redesign, confirmed with the user:

- **Auto-pin, not manual gate.** The app pins folders itself — no more "go do this in Explorer" step, ever. Pin state is checked/managed per **matching top-level folder** (the actual sync-set entries under `HubSyncPath`), not the enclosing parent — the parent was simply the wrong node to check.
- **Pinning is cheap; hydration is not.** Setting `FILE_ATTRIBUTE_PINNED` is a metadata-only flip (instant, no network wait). The real risk the user flagged is a race: if Resource's copy of a folder exists but is still cloud-only placeholders, our own `File.Copy` on it would trigger a synchronous OneDrive download mid-reconcile — a huge stall that could masquerade as locked-file retry spam. So pinning and "safe to touch" are gated separately:
  - **Pin** happens immediately and recursively (once, when a folder transitions from unpinned to managed) — no reason to wait since it's just a flag.
  - **Reconciling that folder** is gated on **hydration completeness** (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` clear on every file, both sides) so the engine never attempts a read that could block on a live download. A not-yet-hydrated top-level folder is excluded from that pass entirely (existence/orphan checks still run fine — only content compare/copy is skipped), logged, and one balloon per app launch tells the user it's downloading. It self-heals into normal sync once hydration finishes, no restart needed.
  - Brand-new folders (no Resource-side copy at all yet) are **not** subject to this wait — there's nothing to hydrate; they're created and pinned immediately as part of the normal first copy, per the user's original ask.

## Implementation

### `Services/Sync/PinHelper.cs`
Add alongside existing `IsPinned`:
- `FILE_ATTRIBUTE_UNPINNED = 0x00100000`, `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000` (documented Cloud Files API bits, same family as the existing `FILE_ATTRIBUTE_PINNED`).
- P/Invoke `SetFileAttributesW` (mirrors the existing `GetAttributes`-based read).
- `Pin(string path)` — best-effort: OR in `PINNED`, clear `UNPINNED`, swallow exceptions (retried next pass, same pattern as `WithRetry` elsewhere).
- `PinTree(string root)` — `Pin(root)` then `Pin` every descendant dir/file via `EnumerateDirectories`/`EnumerateFiles(..., AllDirectories)`, best-effort.
- `IsHydrated(string path)` — `(GetAttributes & RECALL_ON_DATA_ACCESS) == 0`, catch → `true` (nothing to wait for if the path is gone).

Update the class doc comment — it currently says "the app never sets... pinning is a manual Explorer action" — that sentence is being reversed.

### `Services/Sync/SyncEngine.cs`
- New instance fields (reset only by constructing a new engine, i.e. per `FolderSyncService` restart — acceptable, worst case is one extra hydration recheck): `HashSet<string> _confirmedReady` (top-level dirs known fully hydrated — skip rechecking), tracked by dir name.
- In `Reconcile`, after computing `scopeDirs` (existing logic) but before `Scan`:
  - For each dir in `scopeDirs`: if the Hub-side folder isn't pinned, `PinTree` it and log; if the Resource-side folder exists and isn't pinned, same. (Cheap short-circuit via `IsPinned` — the recursive walk only fires on the unpinned→pinned transition.)
  - For each dir not already in `_confirmedReady`: check hydration — enumerate files currently on both sides for that dir (this can reuse `Scan`'s per-file `FileInfo`, so no duplicate directory walk) and test `PinHelper.IsHydrated`. If everything's hydrated, add to `_confirmedReady`. If not, drop that dir from the working scope for this pass only (no Scan/compare/copy for it — existence-based orphan detection earlier in `Reconcile` already ran unaffected) and record it in a new `pendingHydration` set for the result.
- `CopyAcross`: after `Directory.CreateDirectory(...)` and after the final `File.Move(tmp, dest, ...)`, call `PinHelper.Pin` on the new directory/file so anything we write ourselves is immediately pinned — no wait, since we're writing real local content, not reading a placeholder.
- `ReconcileResult`: add `IReadOnlyList<string> PendingHydration` (top-level dir names still waiting on OneDrive this pass).

### `Services/Sync/FolderSyncService.cs`
- Delete the root-level pin-gate entirely: `_pinned`, `_pinGateLogged`, `PinFixInstruction`, and `EvaluateGateAndActivate`'s disable-watchers-when-unpinned branch. Watchers are enabled whenever the service isn't paused — `SyncEngine` now handles skipping not-ready folders internally, so there's no reason to hold the whole service off.
- `Start()`/`Resume()`: simplify to enable watchers + queue full reconcile directly (no gate evaluation).
- `OnTimerTick()`: simplify to just `QueueFull()`.
- `SyncStatus` record: replace `RootsPinned` with `IReadOnlyList<string> PendingHydration`.
- `RunReconcile`: pass `result.PendingHydration` through to the posted `SyncStatus`.

### `TrayApp.cs`
- Remove the old pin-balloon block in `OnSyncStatus` (`!status.RootsPinned` / `PinFixInstruction` / `_pinBalloonShown`).
- Add a new transition-tracked balloon for pending hydration: track previously-notified folder names in a `HashSet<string>` field (fresh per launch, so a relaunch naturally re-notifies if still pending — matches the "notify again each time sync launched" ask without extra logic). When a dir first appears in `PendingHydration`, log + one balloon: `"'{dir}' is still downloading from OneDrive — sync will start automatically once it finishes."` Remove the name from the notified set once it drops out of `PendingHydration` (so a later re-occurrence notifies again).
- `BuildSyncLine`: replace the "OFF — pin folders" branch with a pending-hydration branch, e.g. `Sync: waiting on OneDrive (N folders)` — priority order: Paused > Errors > PendingHydration > OK, mirroring the existing precedence style.

### `Setup/Steps/Resource/SyncFoldersStep.cs`
- Drop the pin check entirely from both `IsAlreadyCompleteAsync` and `RunAsync` — completion becomes "both blank" OR "both set, both exist, not nested" (the existence/nesting validation already there stays).
- Update `Description` to explain pinning and hydration-waiting now happen automatically once the monitor is running — no Explorer step required.

### Docs
- `readme.md` — update the "OneDrive Bridge Folder Sync" section: replace the pinning bullet (currently instructs manual Explorer pinning) with a description of automatic pin + hydration wait, including the one-balloon-per-launch note.
- `Setup/readme.md` — update the Resource Steps table row and the "OneDrive Bridge Folder Sync (Resource)" reference section to match (drop "already-complete = pinned", describe the pin gate as removed from setup and now an ongoing automatic runtime behavior).
- `.vscode/Plans/OneDriveBridge.md` — update the "Pinning (req 3)" bullet: replace "detect-and-gate, NO automation" with the new auto-pin + hydration-gate behavior, and note the root-vs-child-folder lesson learned.

### `Verify-FolderSync.ps1`
- Remove the `attrib +p` pre-seeding of the two roots (no longer a precondition — sync must work starting fully unpinned).
- Add an assertion after the first reconcile that the app itself set the pinned attribute on `Hub\A` and `Res\A` (proves auto-pin ran) — pin state is readable the same way the old script verified it (`attrib`/`GetAttributes`), so this is a straightforward addition to the existing S1 scenario.
- Note in the manual checklist that hydration-gating (the `RECALL_ON_DATA_ACCESS` wait) can't be simulated with plain local files — that bit is only ever set by a real cloud-filter reparse point — so it needs a real OneDrive account for a true end-to-end check: mark a real pre-existing large cloud-only folder as one of Hub's sync-set entries and confirm the tray reports "waiting on OneDrive" until it finishes downloading, then self-heals into normal sync.

## Verification
1. `dotnet build` — must be clean (0 warnings/errors, matching the current baseline).
2. Re-run `Verify-FolderSync.ps1` — all existing scenarios (union merge, propagation, trash, orphan, conflict, delete-vs-edit, locked file, ignores) must still pass unpinned-from-the-start, plus the new pin-was-set assertion.
3. Manual: run `--setup Resource` against a real OneDrive-backed folder pair and confirm the wizard step no longer blocks on the parent path; launch the monitor and confirm sync activates without any Explorer action.
