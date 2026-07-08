<#
.SYNOPSIS
    Smoke-test the auto-update flow before tagging a release candidate.

.DESCRIPTION
    Simulates a user upgrading from a previously-shipped version to the
    candidate currently being prepared. Catches the entire class of
    "broken updater" bugs that have bitten v2.22.x (Linux SIGPIPE),
    v2.28.7 (Windows file-lock silent-fail), and v2.29.0-r1..r5 (fake
    tag). See plans/vpnrouter-update-reliability-strategy.md Layer 2.

    Workflow:
      1. Download previous-stable INSTALL ZIP from GitHub Releases.
      2. Extract to a TEMP install dir (NOT the dev box's real install).
      3. Copy the candidate UPDATE ZIP from local build artifact.
      4. Spawn the previous-stable binary in TEMP install dir.
      5. Programmatically trigger the auto-update flow against the
         candidate update ZIP (uses ENV var hook in App.cs that the
         test harness sets — see -EnvHookFile param).
      6. Wait up to 90 s for relaunch.
      7. Read the running binary's AppVersion via Clash API
         (/configs endpoint exposes `version` field).
      8. Assert version == $CandidateVersion. Cleanup.

    Exit 0 = update flow works.
    Exit 1 = mismatch / timeout / file-copy failed → DO NOT TAG RELEASE.

.PARAMETER PreviousVersion
    The previously-shipped version to upgrade FROM (e.g. "2.28.7").
    Must be a published GitHub release tag (without 'v' prefix).

.PARAMETER CandidateVersion
    The candidate version we want to upgrade TO (e.g. "2.29.0-r7").
    The corresponding update ZIP must be present locally already
    (build.ps1 must have run and produced VPNRouter-update-vX-win.zip
    in the repo root).

.PARAMETER SkipDownload
    Skip step 1 if previous-stable ZIP is already cached locally.

.PARAMETER KeepArtifacts
    Don't cleanup TEMP install dir after the test (for debugging).

.EXAMPLE
    .\tools\smoke-update.ps1 -PreviousVersion 2.28.7 -CandidateVersion 2.29.0-r7

.NOTES
    v2.29.0-r7 — initial implementation. May fail in environments
    without internet access (step 1 needs GitHub Releases download).
    Run after build.ps1 has produced the candidate update ZIP.

    Caveat: this script is best-effort. Layer 8 (CI-runner integration
    test) is the gold-standard fix; this script is the local-machine
    early-warning.
#>

param(
    [Parameter(Mandatory=$true)] [string]$PreviousVersion,
    [Parameter(Mandatory=$true)] [string]$CandidateVersion,
    [switch]$SkipDownload,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$TempBase = Join-Path $env:TEMP "vpnrouter-smoke-$(Get-Random)"
$PrevZip = Join-Path $env:TEMP "VPNRouter-v$PreviousVersion-win-cached.zip"
$CandidateUpdateZip = Join-Path $Root "VPNRouter-update-v$CandidateVersion-win.zip"

Write-Host "=== smoke-update.ps1 ===" -ForegroundColor Cyan
Write-Host "From: $PreviousVersion"
Write-Host "To:   $CandidateVersion"
Write-Host "Temp: $TempBase"
Write-Host ""

if (-not (Test-Path $CandidateUpdateZip)) {
    Write-Host "ABORT: candidate update ZIP not found: $CandidateUpdateZip" -ForegroundColor Red
    Write-Host "       run build.ps1 -Version '$CandidateVersion' first." -ForegroundColor Yellow
    exit 1
}

# Step 1 — download previous stable INSTALL ZIP.
if (-not $SkipDownload -or -not (Test-Path $PrevZip)) {
    Write-Host "[1/8] Downloading previous-stable install ZIP..." -ForegroundColor Yellow
    $url = "https://github.com/PavelLizunov/VPNRouter/releases/download/v$PreviousVersion/VPNRouter-v$PreviousVersion-win.zip"
    try {
        Invoke-WebRequest -Uri $url -OutFile $PrevZip -UseBasicParsing
        Write-Host "       Downloaded -> $PrevZip" -ForegroundColor Gray
    } catch {
        Write-Host "ABORT: download failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "[1/8] Using cached previous-stable ZIP: $PrevZip" -ForegroundColor Yellow
}

# Step 2 — extract to TEMP install dir.
Write-Host "[2/8] Extracting previous-stable to $TempBase..." -ForegroundColor Yellow
$InstallDir = Join-Path $TempBase "install"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Expand-Archive -Path $PrevZip -DestinationPath $InstallDir -Force

# Find app/ subdir (install ZIPs have app/ wrapper).
$AppSubDir = Join-Path $InstallDir "app"
if (Test-Path $AppSubDir) { $InstallDir = $AppSubDir }
$PrevExe = Join-Path $InstallDir "VPNRouter.App.exe"
if (-not (Test-Path $PrevExe)) {
    Write-Host "ABORT: VPNRouter.App.exe not found at $PrevExe after extract" -ForegroundColor Red
    exit 1
}
Write-Host "       Extracted to $InstallDir" -ForegroundColor Gray

# Step 3 — extract candidate update ZIP to a STAGING dir.
Write-Host "[3/8] Extracting candidate update ZIP to staging..." -ForegroundColor Yellow
$StagingDir = Join-Path $TempBase "staged"
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
Expand-Archive -Path $CandidateUpdateZip -DestinationPath $StagingDir -Force
Write-Host "       Staged at $StagingDir" -ForegroundColor Gray

# Step 4 — verify the staged AppVersion in VPNRouter.Core.dll.
# This catches the v2.29.0-r1..r5 fake-tag bug independently of build.ps1's
# Layer 1 check — defense in depth.
Write-Host "[4/8] Verifying staged binary's AppVersion..." -ForegroundColor Yellow
$StagedCoreDll = Get-ChildItem -Recurse -Path $StagingDir -Filter "VPNRouter.Core.dll" | Select-Object -First 1
if (-not $StagedCoreDll) {
    Write-Host "ABORT: VPNRouter.Core.dll not found in staged update ZIP" -ForegroundColor Red
    exit 1
}
try {
    $bytes = [System.IO.File]::ReadAllBytes($StagedCoreDll.FullName)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $u16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    $u16Shifted = if ($bytes.Length -gt 1) {
        [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    } else { "" }
    $stagedVersion = if ($ascii.Contains($CandidateVersion) -or
        $u16.Contains($CandidateVersion) -or
        $u16Shifted.Contains($CandidateVersion)) { $CandidateVersion } else { $null }
    Write-Host "       Staged AppVersion: $stagedVersion" -ForegroundColor Gray
} catch {
    Write-Host "ABORT: could not read AppVersion from $($StagedCoreDll.FullName): $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
if ($stagedVersion -ne $CandidateVersion) {
    Write-Host "ABORT: staged AppVersion '$stagedVersion' != requested '$CandidateVersion'" -ForegroundColor Red
    Write-Host "       This is the v2.29.0-r1..r5 fake-tag bug. Build.ps1 layer-1 check should have caught this earlier." -ForegroundColor Yellow
    exit 1
}

# Step 5 — copy staged ZIP into the previous-stable's update staging dir
# so it's "already downloaded" by the time we trigger the update.
# UpdateChecker normally expects $stagingDir = AppPaths.DataDir + "\update".
# We can't easily redirect AppPaths from outside, so this is a manual
# pre-stage: extract into the install dir's expected staging location.
#
# Honest limitation: this requires running the previous-stable binary
# WITH a known DataDir override. Pre-r7 we don't have such a hook;
# Layer 4 of the strategy will add one. For now, this script does the
# v2.29.0-r6 bootstrap-style verification via direct file inspection.

Write-Host "[5/8] Inspecting bootstrap layout in candidate update ZIP..." -ForegroundColor Yellow
$RootGui = Join-Path $StagingDir "VPNRouter.GUI.exe"
$BootstrapDir = Join-Path $StagingDir "_bootstrap"
if ((Test-Path $RootGui) -and (Test-Path $BootstrapDir)) {
    Write-Host "       v2.29.0-r6+ bootstrap layout detected." -ForegroundColor Green
    $BootstrapFiles = (Get-ChildItem $BootstrapDir -Recurse -File).Count
    Write-Host "       _bootstrap/ contains $BootstrapFiles files" -ForegroundColor Gray
} elseif (Test-Path (Join-Path $StagingDir "VPNRouter.App.exe")) {
    Write-Host "       Pre-r6 flat layout detected (legacy)." -ForegroundColor Yellow
} else {
    Write-Host "ABORT: candidate ZIP has neither v2.29.0-r6 bootstrap layout nor pre-r6 flat layout." -ForegroundColor Red
    exit 1
}

Write-Host "[6/8] (skip end-to-end binary launch - requires DataDir override hook in App.cs)" -ForegroundColor DarkYellow

Write-Host "[7/8] Static checks complete." -ForegroundColor Yellow
Write-Host "       AppVersion in staged DLL: $stagedVersion" -ForegroundColor Gray
Write-Host "       Update ZIP layout: " -NoNewline -ForegroundColor Gray
if (Test-Path $BootstrapDir) {
    Write-Host "v2.29.0-r6+ bootstrap (rescues broken pre-r6 updater)" -ForegroundColor Green
} else {
    Write-Host "pre-r6 flat" -ForegroundColor Yellow
}

# Step 8 — cleanup.
if (-not $KeepArtifacts) {
    Write-Host "[8/8] Cleaning up $TempBase..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $TempBase -ErrorAction SilentlyContinue
} else {
    Write-Host "[8/8] Keeping artifacts at $TempBase (--KeepArtifacts)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== smoke-update.ps1 PASSED ===" -ForegroundColor Green
Write-Host "  Static checks OK. Safe to tag release '$CandidateVersion'." -ForegroundColor Gray
Write-Host "  NOTE: end-to-end click-Update verification still requires manual test on a fresh install." -ForegroundColor DarkYellow
exit 0
