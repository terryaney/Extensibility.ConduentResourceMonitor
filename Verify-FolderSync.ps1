# Verify-FolderSync.ps1 — end-to-end scenarios for the OneDrive bridge folder sync.
# Runnable on any dev box: the engine is OneDrive-agnostic and the pinned bit (attrib +p) is
# settable on any NTFS folder, so this script starts fully unpinned and asserts the app pins
# folders itself. NOTE: the hydration gate (RECALL_ON_DATA_ACCESS) can't be simulated with plain
# local files — only a live cloud-filter reparse point sets that bit — so it needs a real OneDrive
# account for a true end-to-end check (see manual checklist in OneDriveBridge.md).
param(
	[string]$ExeDir = (Join-Path $PSScriptRoot 'bin\Debug\net10.0-windows'),
	[int]$TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $ExeDir 'ConduentResourceMonitor.exe'
if (-not (Test-Path $exe)) { throw "Not found: $exe — run 'dotnet build' first." }

# $env:TEMP can be an 8.3 short path (e.g. C:\Users\TERRY~1.ANE\...) whose '~' trips up
# PowerShell path resolution in Remove-Item; LOCALAPPDATA is the same location in long form.
$testRoot = Join-Path $env:LOCALAPPDATA 'Temp\CrmSyncTest'
$hub = Join-Path $testRoot 'Hub'
$res = Join-Path $testRoot 'Res'
$settingsPath = Join-Path $ExeDir 'ResourceMonitor.settings.json'
$backupPath = "$settingsPath.verify-backup"
$ledgerPath = Join-Path $ExeDir 'sync.ledger.json'
$trashDir = Join-Path $ExeDir 'SyncTrash'
$conflictDir = Join-Path $ExeDir 'SyncConflict'
$syncLogPath = Join-Path $ExeDir 'sync.log'

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:Proc = $null

function Assert([string]$Name, [bool]$Condition) {
	$script:Results.Add([pscustomobject]@{ Name = $Name; Pass = $Condition })
	$tag = if ($Condition) { 'PASS' } else { 'FAIL' }
	$color = if ($Condition) { 'Green' } else { 'Red' }
	Write-Host "  [$tag] $Name" -ForegroundColor $color
}

function Wait-Until([scriptblock]$Condition, [int]$Timeout = $TimeoutSec) {
	$deadline = (Get-Date).AddSeconds($Timeout)
	while ((Get-Date) -lt $deadline) {
		try { if (& $Condition) { return $true } } catch {}
		Start-Sleep -Milliseconds 500
	}
	try { return [bool](& $Condition) } catch { return $false }
}

function Start-App {
	$script:Proc = Start-Process $exe -PassThru
	Start-Sleep -Seconds 2
}

function Stop-App {
	if ($script:Proc -and -not $script:Proc.HasExited) { Stop-Process -Id $script:Proc.Id -Force }
	$script:Proc = $null
	Start-Sleep -Seconds 1
}

function Get-Text([string]$Path) {
	if (-not (Test-Path $Path)) { return $null }
	(Get-Content $Path -Raw).Trim()
}

function Get-TrashCount {
	if (-not (Test-Path $trashDir)) { return 0 }
	@(Get-ChildItem $trashDir -Recurse -File).Count
}

function Test-Pinned([string]$Path) {
	if (-not (Test-Path $Path)) { return $false }
	(([int](Get-Item $Path -Force).Attributes) -band 0x00080000) -ne 0
}

try {
	# --- Environment setup -------------------------------------------------------------------
	Write-Host "== Setup ==" -ForegroundColor Cyan
	if (Test-Path $testRoot) { Remove-Item $testRoot -Recurse -Force }
	New-Item -ItemType Directory -Force -Path "$hub\A", "$res\A", "$hub\C", "$res\C" | Out-Null
	# C simulates a folder the user already pinned in Explorer before ever running this app —
	# confirms the app leaves an already-pinned folder alone and still syncs it normally.
	attrib +p "$hub\C"
	attrib +p "$res\C"

	if ((Test-Path $settingsPath) -and -not (Test-Path $backupPath)) {
		Copy-Item $settingsPath $backupPath
	}
	@{
		Mode                 = 'Resource'
		CheckUrl             = 'https://hrspwebtools001.americas.oneacs.com/msl'
		ProxyAddress         = 'localhost:8888'
		HubSyncPath          = $hub
		ResourceSyncPath     = $res
		SyncIgnorePatterns   = '~$*;*.tmp;desktop.ini;Thumbs.db'
		SyncPaused           = $false
		CheckIntervalSeconds = 30
		NotifyTimeoutMs      = 5000
	} | ConvertTo-Json | Set-Content $settingsPath

	foreach ($stale in @($ledgerPath, $syncLogPath, "$syncLogPath.1")) {
		if (Test-Path $stale) { Remove-Item $stale -Force }
	}
	foreach ($staleDir in @($trashDir, $conflictDir)) {
		if (Test-Path $staleDir) { Remove-Item $staleDir -Recurse -Force }
	}

	# --- S1: first-run union merge (seed BEFORE launch) ---------------------------------------
	Write-Host "== S1: First-run union merge ==" -ForegroundColor Cyan
	Set-Content "$hub\A\1.txt" 'one-hub'
	Set-Content "$res\A\2.txt" 'two-res'
	Set-Content "$hub\A\3.txt" 'three-hub-newer'
	Set-Content "$res\A\3.txt" 'three-res-older'
	(Get-Item "$res\A\3.txt").LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddHours(-1)
	Set-Content "$hub\C\pre.txt" 'pre-pinned'

	Start-App
	Assert 'S1 ledger created' (Wait-Until { Test-Path $ledgerPath })
	Assert 'S1 hub-only file copied to Res' (Wait-Until { (Get-Text "$res\A\1.txt") -eq 'one-hub' })
	Assert 'S1 res-only file copied to Hub' (Wait-Until { (Get-Text "$hub\A\2.txt") -eq 'two-res' })
	Assert 'S1 newer Hub wins differing file' (Wait-Until { (Get-Text "$res\A\3.txt") -eq 'three-hub-newer' })
	Assert 'S1 loser preserved in SyncConflict' (Wait-Until { @(Get-ChildItem "$conflictDir\A" -Filter '3.txt.Resource.*' -ErrorAction SilentlyContinue).Count -eq 1 })
	Assert 'S1 nothing deleted' ((Get-TrashCount) -eq 0)
	Assert 'S1 app auto-pinned Hub\A' (Wait-Until { Test-Pinned "$hub\A" })
	Assert 'S1 app auto-pinned Res\A' (Wait-Until { Test-Pinned "$res\A" })
	Assert 'S1 already-pinned folder still syncs' (Wait-Until { (Get-Text "$res\C\pre.txt") -eq 'pre-pinned' })
	Assert 'S1 already-pinned folder remains pinned' ((Test-Pinned "$hub\C") -and (Test-Pinned "$res\C"))

	# --- S2: propagation ----------------------------------------------------------------------
	Write-Host "== S2: Propagation ==" -ForegroundColor Cyan
	Set-Content "$hub\A\new.txt" 'brand-new'
	Set-Content "$res\A\1.txt" 'one-res-edited'
	Assert 'S2 Hub add propagates' (Wait-Until { (Get-Text "$res\A\new.txt") -eq 'brand-new' })
	Assert 'S2 Res edit propagates' (Wait-Until { (Get-Text "$hub\A\1.txt") -eq 'one-res-edited' })
	$mtimeDelta = [math]::Abs(((Get-Item "$hub\A\new.txt").LastWriteTimeUtc - (Get-Item "$res\A\new.txt").LastWriteTimeUtc).TotalSeconds)
	Assert 'S2 mtimes match within 2s' ($mtimeDelta -le 2)

	# --- S3: delete -> trash ------------------------------------------------------------------
	Write-Host "== S3: Delete propagates to SyncTrash ==" -ForegroundColor Cyan
	Remove-Item "$hub\A\1.txt"
	Assert 'S3 Res copy removed' (Wait-Until { -not (Test-Path "$res\A\1.txt") })
	Assert 'S3 Res copy in SyncTrash' (@(Get-ChildItem "$trashDir\A" -Filter '1.txt.*' -ErrorAction SilentlyContinue).Count -eq 1)
	$baselineTrash = Get-TrashCount

	# --- S4: hub-side top-dir removal = orphan (no deletes) ------------------------------------
	Write-Host "== S4: Orphan rule ==" -ForegroundColor Cyan
	Remove-Item "$hub\A" -Recurse -Force
	Assert 'S4 ORPHAN logged' (Wait-Until { (Test-Path $syncLogPath) -and ((Get-Content $syncLogPath -Raw) -match 'ORPHAN') })
	Start-Sleep -Seconds 8  # let any (wrong) delete propagation surface before asserting
	Assert 'S4 Res copy untouched' ((Test-Path "$res\A\2.txt") -and (Test-Path "$res\A\3.txt") -and (Test-Path "$res\A\new.txt"))
	Assert 'S4 no new trash entries' ((Get-TrashCount) -eq $baselineTrash)

	New-Item -ItemType Directory -Force -Path "$hub\A" | Out-Null
	Set-Content "$hub\A\4.txt" 'four-hub'
	Assert 'S4 re-added dir merges additively' (Wait-Until { (Test-Path "$hub\A\2.txt") -and (Get-Text "$res\A\4.txt") -eq 'four-hub' })
	Assert 'S4 additive merge deleted nothing' ((Get-TrashCount) -eq $baselineTrash)

	# --- S5: resource-side top-dir delete = ordinary deletes -----------------------------------
	Write-Host "== S5: Resource-side top-dir delete ==" -ForegroundColor Cyan
	New-Item -ItemType Directory -Force -Path "$hub\B" | Out-Null
	Set-Content "$hub\B\b1.txt" 'bee-one'
	Set-Content "$hub\B\b2.txt" 'bee-two'
	Assert 'S5 B propagated to Res' (Wait-Until { (Test-Path "$res\B\b1.txt") -and (Test-Path "$res\B\b2.txt") })
	Remove-Item "$res\B" -Recurse -Force
	Assert 'S5 Hub copies moved to SyncTrash' (Wait-Until {
		@(Get-ChildItem "$trashDir\B" -Filter 'b1.txt.*' -ErrorAction SilentlyContinue).Count -eq 1 -and
		@(Get-ChildItem "$trashDir\B" -Filter 'b2.txt.*' -ErrorAction SilentlyContinue).Count -eq 1
	})
	Assert 'S5 Hub files gone' (-not (Test-Path "$hub\B\b1.txt") -and -not (Test-Path "$hub\B\b2.txt"))

	# --- S6: offline conflict (both sides changed, Hub newer) -----------------------------------
	Write-Host "== S6: Conflict (offline edits) ==" -ForegroundColor Cyan
	Stop-App
	Set-Content "$hub\A\2.txt" 'two-hub-conflict'
	Set-Content "$res\A\2.txt" 'two-res-conflict'
	(Get-Item "$res\A\2.txt").LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddHours(-1)
	Start-App
	Assert 'S6 Hub content on both sides' (Wait-Until { (Get-Text "$res\A\2.txt") -eq 'two-hub-conflict' -and (Get-Text "$hub\A\2.txt") -eq 'two-hub-conflict' })
	Assert 'S6 Res version in SyncConflict' (@(Get-ChildItem "$conflictDir\A" -Filter '2.txt.Resource.*' -ErrorAction SilentlyContinue).Count -eq 1)

	# --- S7: delete-vs-edit race — edit wins ----------------------------------------------------
	Write-Host "== S7: Delete-vs-edit ==" -ForegroundColor Cyan
	Stop-App
	Remove-Item "$hub\A\3.txt"
	Set-Content "$res\A\3.txt" 'three-res-resurrected'
	Start-App
	Assert 'S7 edited copy resurrected on Hub' (Wait-Until { (Get-Text "$hub\A\3.txt") -eq 'three-res-resurrected' })

	# --- S8: locked file retry/heal -------------------------------------------------------------
	Write-Host "== S8: Locked file ==" -ForegroundColor Cyan
	$lock = [System.IO.File]::Open("$res\A\4.txt", 'Open', 'Read', 'None')
	try {
		Set-Content "$hub\A\4.txt" 'four-updated'
		Assert 'S8 retry/error logged' (Wait-Until { (Get-Content $syncLogPath -Raw) -match "(RETRY|ERROR).*4\.txt" } 45)
	}
	finally {
		$lock.Close()
	}
	Set-Content "$hub\A\4.txt" 'four-updated-2'  # new FSW event triggers the healing pass
	Assert 'S8 next pass heals' (Wait-Until { (Get-Text "$res\A\4.txt") -eq 'four-updated-2' } 45)

	# --- S9: ignore patterns --------------------------------------------------------------------
	Write-Host "== S9: Ignores ==" -ForegroundColor Cyan
	Set-Content (Join-Path $hub 'A\~$doc.docx') 'ignored'
	Set-Content "$hub\A\x.tmp" 'ignored'
	Set-Content "$hub\A\desktop.ini" 'ignored'
	Set-Content "$hub\A\real.txt" 'real'
	Assert 'S9 real file synced' (Wait-Until { (Get-Text "$res\A\real.txt") -eq 'real' })
	Assert 'S9 ignored files not copied' (-not (Test-Path "$res\A\~`$doc.docx") -and -not (Test-Path "$res\A\x.tmp") -and -not (Test-Path "$res\A\desktop.ini"))
	$ledgerRaw = Get-Content $ledgerPath -Raw
	Assert 'S9 ignored files absent from ledger' (($ledgerRaw -notmatch 'doc\.docx') -and ($ledgerRaw -notmatch 'x\.tmp') -and ($ledgerRaw -notmatch 'desktop\.ini'))
}
finally {
	# --- Teardown -------------------------------------------------------------------------------
	Stop-App
	if (Test-Path $backupPath) {
		Move-Item $backupPath $settingsPath -Force
	}
	elseif (Test-Path $settingsPath) {
		Remove-Item $settingsPath -Force
	}
}

Write-Host ""
Write-Host "== Summary ==" -ForegroundColor Cyan
$fails = @($script:Results | Where-Object { -not $_.Pass })
$script:Results | ForEach-Object {
	$tag = if ($_.Pass) { 'PASS' } else { 'FAIL' }
	$color = if ($_.Pass) { 'Green' } else { 'Red' }
	Write-Host "  [$tag] $($_.Name)" -ForegroundColor $color
}
Write-Host ""
Write-Host ("{0}/{1} assertions passed." -f ($script:Results.Count - $fails.Count), $script:Results.Count)
if ($fails.Count -gt 0) { exit 1 }
exit 0
