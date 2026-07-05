# OneDrive Bridge Folder Sync (Resource machine)

## Context

The Resource machine is the only box with local access to BOTH OneDrive accounts (Hub account + Resource account). Files produced by scripts on Travel/Hub or VDI need to flow to the other side; the app will mirror two local folders on the Resource machine — one under each OneDrive account's local root — and let the OneDrive clients handle cloud transport. The feature lives inside the existing ConduentResourceMonitor tray app (net10.0-windows, WinForms, System.Text.Json settings).

## Decided semantics (interviewed & locked — do not redesign)

- **Paths**: `HubSyncPath` + `ResourceSyncPath` (Resource mode only, both required together, optional overall). Blank = feature off. No path settings on Hub/Travel modes — native OneDrive handles those machines entirely.
- **Sync set is dynamic**: top-level dirs under `HubSyncPath` define it; same-named dirs under `ResourceSyncPath` mirror recursively; other Resource-root content ignored.
- **Ledger** (`sync.ledger.json`, app dir): last-known per-side state (size + LastWriteTimeUtc per relPath). Deletes fire ONLY from ledger transitions, never from raw FSW events or absence alone. Missing root dir = error, never mass delete.
- **Soft delete**: propagated deletes move counterpart to `SyncTrash\{relPath}.{yyyyMMdd-HHmmss}` (app dir), logged. Recycle Bin rejected (can't count/purge our own entries reliably).
- **Orphan rule**: top-level folder removed from **Hub** root → leave Resource copy, drop ledger entries, log ORPHAN, stop syncing. Top-level folder removed from **Resource** side → ordinary deletes (Hub copies → SyncTrash). Asymmetry is intentional.
- **Conflicts** (both sides changed vs ledger, differ): newer LastWriteTimeUtc wins; loser → `SyncConflict\{relDir}\{name}.{side}.{stamp}`; logged. 2s timestamp tolerance.
- **Delete-vs-edit race**: edit wins — edited copy resurrected over the delete, logged.
- **First run (no ledger)**: additive union merge — copy missing both ways, newer-wins for differing (loser → SyncConflict), NO deletes.
- **Engine**: FSW on both roots (debounced ~3s quiet window → targeted reconcile of dirty top-level dirs) + full reconcile every 10 min and at startup. Resource mode only.
- **Pause/Resume** via tray menu; `SyncPaused` persisted in settings (survives restart).
- **Icon**: sync-arrows glyph while active (green = all checks pass, red = any check fails — sync errors NEVER change color); plain circle when paused, unconfigured, or pin-gated.
- **Tooltip**: existing check lines + `Sync: OK (last HH:mm)` / `Sync: N errors` / `Sync: Paused` + `SyncTrash: N files` (when >0). Truncate to 127 chars (NotifyIcon.Text limit — fixes latent bug too).
- **Balloon**: one `ShowBalloonTip` on healthy→erroring transition only; recovery resets the flag.
- **sync.log** (app dir): timestamped lines, 1MB rollover to `sync.log.1`, mirrored to LogForm.
- **Pinning (req 3) — superseded, see [OneDrive.Bridge.Pinning.md](OneDrive.Bridge.Pinning.md)**: the original detect-and-gate design (below, struck through for history) checked `FILE_ATTRIBUTE_PINNED` on the *root* `HubSyncPath`/`ResourceSyncPath` and blocked all of sync if either was unpinned. Live testing showed this was wrong: the root of a OneDrive library (e.g. `C:\Users\x\OneDrive - Conduent`) never itself carries the pinned attribute even when its actual sync-set children do — the parent was simply the wrong node to check. Redesigned to auto-pin + per-folder hydration gate: the app pins each matching top-level folder itself (cheap, instant, recursive, on the unpinned→managed transition), and separately gates actual content reconcile on hydration (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` clear on every file, both sides) so a pin-and-read of a cloud-only placeholder can never stall the engine. See that doc for full details.
  - ~~Detection = `FILE_ATTRIBUTE_PINNED` bit (0x00080000) on the root folder via `File.GetAttributes` (cast to int — the named enum lacks it). If either root is unpinned: sync stays inactive, tooltip shows "Sync: OFF — folders not pinned", one balloon with the manual fix ("right-click folder → Always keep on this device"), logged. Re-checked at Start/Resume and each 10-min tick, so pinning it in Explorer auto-enables sync without restart. User pins manually on VDI/Travel machines too (no app involvement there).~~
- **Ignores** (file-name match, OrdinalIgnoreCase): `SyncIgnorePatterns` setting, semicolon-delimited, default `~$*;*.tmp;desktop.ini;Thumbs.db`; editable in the sync setup step and the settings dialog.

## New files

### `Services/Sync/SyncLedger.cs`
```csharp
class LedgerFileState { long Size; DateTime LastWriteUtc; }
class LedgerEntry { LedgerFileState? Hub; LedgerFileState? Resource; }
class SyncLedger {
    int Version = 1;
    List<string> SyncedRoots;                       // top-level dir names — powers orphan detection
    Dictionary<string, LedgerEntry> Entries;        // key = relPath, OrdinalIgnoreCase
    static SyncLedger? Load(string path);           // null (not empty) when absent → first-run
    void Save(string path);                         // write .tmp then File.Move(overwrite) — atomic
}
```
Per-side state is deliberate: OneDrive can rewrite mtimes during hydration; "hubChanged"/"resChanged" evaluate independently.

### `Services/Sync/SyncEngine.cs` — pure reconcile, no timers/FSW (testable with two temp dirs)
```csharp
record ReconcileResult(SyncLedger Ledger, int Copies, int Trashed, int Conflicts, int Orphans, int Errors, string? FirstError);
class SyncEngine {
    SyncEngine(string hubRoot, string resourceRoot, string trashDir, string conflictDir,
               IReadOnlyList<string> ignorePatterns, SyncLog log);   // patterns parsed from settings.SyncIgnorePatterns
    ReconcileResult Reconcile(SyncLedger? ledger, IReadOnlySet<string>? onlyTopDirs, CancellationToken ct); // null = full
}
```
Algorithm (3-way compare per relPath in union of Hub scan, Resource scan, ledger scope):
- Guard: either root missing → error result, no ops.
- Orphan check runs on every pass (full AND targeted, scoped to the dirty top dirs being processed): `ledger.SyncedRoots − topDirsNow` (within scope) = orphans → drop entries, log, no file ops. Scoping it to targeted passes too (not full-only) closes a race: a Hub top-dir delete can reach the engine as child-file events (marking the dir dirty) before the root-level event forces a full pass, and a targeted pass that skipped orphan detection would misread the missing dir as per-file Resource deletes and mass-trash the Resource copy.
- `l == null`: one side only → copy across; both same → record; both differ → newer wins, loser → SyncConflict.
- `l != null`: `changed(x, side)` = existence/size/mtime(>2s) drift vs that side's ledger state.
  - both absent → drop entry. One absent: other side changed → **edit wins, resurrect**; else → **soft delete to SyncTrash**, drop entry.
  - both present: one changed → copy that direction; both changed + same → refresh entry (OneDrive echo); both changed + differ → conflict rule.
- Copy mechanics: copy to `dest + ".crmsync-tmp"`, set LastWriteTimeUtc, `File.Move(tmp, dest, overwrite: true)`. Why: a direct `File.Copy` writes the destination progressively, and OneDrive (watching that folder) can start uploading — and the far machine can receive — a half-written file. A same-volume rename is atomic on NTFS, so the real filename only ever shows the complete old or complete new content. Scans skip `.crmsync-tmp` so a reconcile running mid-copy ignores our own temp files.
- Locked files: 3 retries (500ms/2s/5s) on IOException/UnauthorizedAccessException; final failure → log, Errors++, leave ledger entry unchanged so next pass retries.
- Self-echo is structural: our copies raise FSW on the other root → next targeted pass finds both sides matching ledger → no-op. No suppression bookkeeping.

### `Services/Sync/FolderSyncService.cs` — orchestrator
```csharp
record SyncStatus(bool Active, bool Paused, bool RootsPinned, DateTime? LastSyncLocal, int ErrorCount, string? LastError, int TrashFileCount);
class FolderSyncService : IDisposable {
    FolderSyncService(AppSettings settings, Action<string>? uiLog); // construct on UI thread — captures SynchronizationContext
    event Action<SyncStatus>? StatusChanged;                        // Posted to UI context
    void Start(); void Pause(); void Resume(); void RequestFullReconcile();
    int GetTrashFileCount(); void PurgeTrash(); void Stop();
}
```
Threading: single worker Task + coalesced flags under one lock (`bool _fullPending`, `HashSet<string> _dirtyTopDirs`, `DateTime _lastFsEventUtc`, `SemaphoreSlim _wake`). Exactly one reconcile at a time; full supersedes targeted.
- Two FSWs (hub/resource roots): `IncludeSubdirectories = true`, `InternalBufferSize = 64KB`, `NotifyFilter = FileName|DirectoryName|LastWrite|Size`. All events → extract first path segment → mark dirty top dir + stamp + wake. Event at root itself (top dir create/rename) → `_fullPending`. FSW `Error` (buffer overflow) → `_fullPending`.
- Worker: wait wake → loop 500ms delays until 3s quiet or fullPending → snapshot+clear flags → `Reconcile(...)` in try/catch → `ledger.Save()` → post StatusChanged.
- `System.Threading.Timer` 10 min → fullPending. Startup = initial fullPending in `Start()` (skipped if `SyncPaused`).
- Pause = disable FSWs + timer + clear flags; Resume = re-enable + queue full reconcile.
- **Pin gate**: at `Start()`, `Resume()`, and each 10-min tick, check `PinHelper.IsPinned` on both roots. Either unpinned → FSWs stay disabled, no reconciles run, status = `Active: false, RootsPinned: false`, one log line with the manual fix. Once the periodic check sees both pinned → enable watchers, queue full reconcile — sync self-heals without restart.
- Targeted granularity is whole top-level dir (coarse on purpose — personal scale, avoids path bookkeeping bugs).

### `Services/Sync/SyncLog.cs`
`SyncLog(string path, long maxBytes = 1_000_000, Action<string>? mirror)`; `Line(msg)` = lock { rollover-check; append `[yyyy-MM-dd HH:mm:ss] msg` }. Rollover = move to `sync.log.1` (overwrite).

### `Services/Sync/PinHelper.cs`
Detection only — the app never sets the attribute.
```csharp
static class PinHelper {
    const int FILE_ATTRIBUTE_PINNED = 0x00080000;
    static bool IsPinned(string path) => ((int)File.GetAttributes(path) & FILE_ATTRIBUTE_PINNED) != 0;
}
```

### `Setup/Steps/Resource/SyncFoldersStep.cs` (Resource mode only)
Pattern: [HostsFileStep](Setup/Steps/Shared/HostsFileStep.cs) — step with `Inputs`.
- Inputs: `ctx.HubSyncPathInput()`, `ctx.ResourceSyncPathInput()` (`Kind = FolderPath`), `ctx.SyncIgnorePatternsInput()` (`Kind = Text`, pre-filled with default). Blank paths valid (feature off); non-blank must exist; cross-field check (not identical/nested) in `RunAsync`.
- `RunAsync`: blank paths → `SetupStepResult(true, "Skipped — no sync folders configured.")`. Otherwise verify `PinHelper.IsPinned` on both roots; unpinned → `SetupStepResult(false, "Right-click '<path>' in Explorer → Always keep on this device, then re-run this step.")`. Both pinned → success.
- `IsAlreadyCompleteAsync` = both paths blank OR (both exist AND both pinned).

## Changes to existing files

- **AppSettings.cs**: add `HubSyncPath`, `ResourceSyncPath` (both `""`), `string SyncIgnorePatterns = "~$*;*.tmp;desktop.ini;Thumbs.db"`, `bool SyncPaused`; `[JsonIgnore] bool SyncConfigured` (Resource mode + both paths nonblank). Validation: exactly-one-set is an error; both set → must exist and not be nested in each other.
- **Setup/SetupContext.cs**: `HubSyncPath`, `ResourceSyncPath`, `SyncIgnorePatterns` props + three `SetupInput` factory methods; `PersistInputs` persists them unconditionally (clearing a path in setup turns feature off).
- **Program.cs** `RunSetup`: pre-fill new ctx fields from settings (mirrors existing `HubStaticIp`/`ConfFilePath` pre-fill ~L117-124).
- **Setup/StepFactory.cs**: append `SyncFoldersStep` to the Resource list only (before `StartupShortcutStep`).
- **TrayApp.cs** (largest):
  - Fields: `FolderSyncService? _sync; SyncStatus? _syncStatus; bool _lastAllOk = true; bool _syncErrorBalloonShown;` + `_greenSyncIcon/_redSyncIcon`.
  - Ctor (Resource mode + `SyncConfigured`): create service, subscribe `StatusChanged`, `Start()`.
  - New `UpdateTrayPresentation()` owns icon + tooltip; called from `OnResultsUpdated` (~L138, after storing `_lastAllOk`) and `OnSyncStatus`. Icon = sync glyph iff `Active && !Paused`, colored by `_lastAllOk`; plain circle otherwise (incl. unpinned). Tooltip = check lines + sync line (`Sync: OK (last HH:mm)` / `Sync: N errors` / `Sync: Paused` / `Sync: OFF — pin folders`) + trash count, truncated to 127 chars.
  - `OnSyncStatus`: balloon once on 0→N errors AND once on pinned→unpinned ("Sync disabled — right-click the sync folders in Explorer → Always keep on this device"); reset flags on recovery.
  - `RebuildMenu` (~L197): when `_sync != null` add "Pause Syncing"/"Resume Syncing" (persist `SyncPaused`, call Pause/Resume) and "Purge Sync Trash (N files)" (Enabled when N>0; MessageBox YesNo confirm → `PurgeTrash()`), + separator.
  - Settings handler (~L213): if sync paths changed → stop/dispose/recreate `_sync` (mirrors `_pacServer` restart pattern).
  - `Shutdown`/`Dispose`: stop + dispose service and new icons.
  - New `CreateSyncIcon(Color)` beside `CreateCircleIcon` (~L327): 32px bitmap, two 140° arcs (`DrawArc` 200→340 and 20→160, 4px round-cap pen) + filled triangle arrowheads tangent at arc ends. Iterate visually — renders at 16px.
- **SettingsForm.cs**: Resource-mode-only rows "Hub Sync Path" / "Resource Sync Path" / "Sync Ignore Patterns" (semicolon-delimited) using existing field/label dictionary + hide pattern. `SyncPaused` NOT in settings form (tray owns it).

## Implementation order

1. AppSettings properties + validation.
2. SyncLog + PinHelper (leaf utilities).
3. SetupContext inputs + SyncFoldersStep + StepFactory + RunSetup pre-fill (setup/pin-detection shippable alone).
4. SyncLedger + SyncEngine (the careful algorithm work; test with two temp dirs, no UI).
5. FolderSyncService (FSW + debounce + worker + status).
6. TrayApp integration + SettingsForm rows.
7. `Verify-FolderSync.ps1` + manual checklist run.

## Verification

### Automated: `Verify-FolderSync.ps1` (new file, repo root)
Runnable on the Resource machine (or any dev box — the engine is OneDrive-agnostic). The script:
1. Creates two temp roots under `$env:TEMP\CrmSyncTest\{Hub,Res}` and runs `attrib +p` on both (the pinned bit is settable on any NTFS folder, so the pin gate passes without OneDrive).
2. Backs up `ResourceMonitor.settings.json`, writes a test copy pointing `HubSyncPath`/`ResourceSyncPath` at the temp roots (mode Resource), deletes any `sync.ledger.json`, launches the app.
3. Runs scenarios sequentially, each = perform file ops → wait out debounce (sleep ~8s, poll up to ~30s) → assert → report PASS/FAIL:
   - **First run union merge**: seed `Hub\A\1.txt`, `Res\A\2.txt`, differing `A\3.txt` both sides (Hub newer) BEFORE launch → union on both sides, Res `3.txt` in `SyncConflict\A\`, nothing deleted, ledger exists.
   - **Propagation**: add `Hub\A\new.txt`, edit `Res\A\1.txt` → both propagate, mtimes match within 2s.
   - **Delete → trash**: delete `Hub\A\1.txt` → Res copy in `SyncTrash\A\`, gone from `Res\A`.
   - **Orphan**: delete top-level `Hub\A` → `Res\A` untouched; sync.log contains ORPHAN. Recreate `Hub\A` with a file → additive merge, no deletes.
   - **Resource-side top-dir delete**: delete `Res\B` → all `Hub\B` files in SyncTrash.
   - **Conflict**: stop app; edit both sides of one file (Hub newer); relaunch → Hub content both sides, Res version in SyncConflict.
   - **Del-vs-edit**: stop app; delete on Hub, edit on Res; relaunch → file resurrected on Hub.
   - **Locked file**: hold exclusive handle on a Res file, edit Hub side → sync.log shows retry/error; release → next pass heals.
   - **Ignores**: create `~$doc.docx`, `x.tmp`, `desktop.ini` under Hub → never copied, absent from ledger.
4. Kills the app, restores the real settings file, prints scenario summary.

### Manual checklist (visual/UX — can't be scripted)
1. **Icons**: unconfigured → plain circle; configured+active → sync arrows; fail a monitored check → red arrows (sync errors alone must NOT change color).
2. **Auto-pin**: `attrib -p` a managed top-level folder, relaunch (or wait for the next reconcile pass) → app re-pins it itself, logged; confirm with `attrib` that the bit is set again — no manual Explorer action.
3. **Hydration gate** (needs a real OneDrive account — plain local files can't fake `RECALL_ON_DATA_ACCESS`, only a live cloud-filter reparse point sets it): mark a real pre-existing large cloud-only folder as one of Hub's sync-set entries; confirm the tray reports "waiting on OneDrive" and content sync is skipped for that folder until it finishes downloading, then self-heals into normal sync with no restart.
4. **Tray menu**: Pause Syncing → persists across kill/relaunch (tooltip "Sync: Paused"); Resume → full reconcile catches offline edits. Purge Sync Trash shows live count, confirm dialog, empties folder.
5. **Balloon**: exactly one per healthy→erroring transition; resets after recovery. Exactly one per folder entering pending-hydration; resets once it drops out.
6. **Setup**: `--setup Resource` → SyncFoldersStep captures paths + ignore patterns; no pin check — completes as soon as both paths exist and aren't nested.
7. Build: `dotnet build` per existing tasks.json; publish flow unchanged.
