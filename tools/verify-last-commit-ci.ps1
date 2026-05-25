# verify-last-commit-ci.ps1 - hard precondition for ship-rolling-candidate.
#
# v2.37.0-r19 lesson: shipped r7..r18 in one session without checking
# commit-level CI on each push. Tag-level CI (build-mac, build-linux on
# tag push) was green because the bug was a Linux-only Avalonia XAML
# binding to a property gated by `#if PLATFORM_WINDOWS`. Commit-level
# CI (Build Linux + Build macOS + dotnet test on push event) was red
# the whole time. User caught it via screenshot of red-X commits column.
#
# This script enforces "previous commit must have GREEN commit-CI" as a
# precondition for shipping the next -rN. Bail loudly if anything is
# red, skipped (where unexpected), or in_progress.
#
# Usage (from ship-rolling-candidate skill, BEFORE bumping AppVersion):
#   powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1
#
# Exit codes:
#   0 - previous commit CI green; safe to ship next -rN
#   1 - previous commit CI red OR contains unexpected failures
#   2 - previous commit CI still in_progress (wait + retry)
#   3 - gh CLI missing or auth issue

param(
    [string]$Repo = $(if ($env:REPO) { $env:REPO } else { "PavelLizunov/VPNRouter" }),
    [string]$IgnoreSkipped = $(if ($env:IGNORE_SKIPPED) { $env:IGNORE_SKIPPED } else { "Build Android APK" }),
    [string]$TolerateFailure = $(if ($env:TOLERATE_FAILURE) { $env:TOLERATE_FAILURE } else { "" })
)

$ErrorActionPreference = "Stop"

$head = (git rev-parse HEAD).Trim()
if (-not $head) {
    Write-Host "ERROR: could not resolve HEAD SHA" -ForegroundColor Red
    exit 3
}
Write-Host ("Verifying CI for HEAD: " + $head) -ForegroundColor Cyan

try {
    $null = (gh auth status 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: gh CLI not authenticated. Run 'gh auth login'." -ForegroundColor Red
        exit 3
    }
}
catch {
    Write-Host "ERROR: gh CLI missing." -ForegroundColor Red
    exit 3
}

$apiPath = "repos/" + $Repo + "/commits/" + $head + "/check-runs?per_page=30"
$json = gh api $apiPath 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ("ERROR: gh api failed: " + $json) -ForegroundColor Red
    exit 3
}

$data = $json | ConvertFrom-Json
$checks = $data.check_runs

if (-not $checks -or $checks.Count -eq 0) {
    Write-Host "WARN: no check-runs returned. CI may not have fired yet (wait ~30s, retry)." -ForegroundColor Yellow
    exit 2
}

$skipOk = @{}
foreach ($n in $IgnoreSkipped.Split(',')) {
    $t = $n.Trim()
    if ($t) { $skipOk[$t] = $true }
}
$failOk = @{}
foreach ($n in $TolerateFailure.Split(',')) {
    $t = $n.Trim()
    if ($t) { $failOk[$t] = $true }
}

$hardRed = @()
$inProgress = @()
$tolerated = @()
$green = 0
foreach ($c in $checks) {
    $name = $c.name
    $conclusion = $c.conclusion
    $status = $c.status

    if ($status -ne "completed") {
        $inProgress += ($name + " [" + $status + "]")
        continue
    }

    if ($conclusion -eq "success") {
        $green++
    }
    elseif ($conclusion -eq "skipped") {
        if ($skipOk.ContainsKey($name)) {
            $tolerated += ($name + " (skipped, expected)")
        } else {
            $tolerated += ($name + " (skipped, unexpected - review)")
        }
    }
    elseif ($conclusion -eq "failure") {
        if ($failOk.ContainsKey($name)) {
            $tolerated += ($name + " (failure, tolerated)")
        } else {
            $hardRed += ($name + " [" + $c.html_url + "]")
        }
    }
    elseif ($conclusion -eq "cancelled") {
        $tolerated += ($name + " (cancelled)")
    }
    else {
        $hardRed += ($name + " [" + $conclusion + " - " + $c.html_url + "]")
    }
}

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host ("  Green:        " + $green) -ForegroundColor Green
Write-Host ("  Tolerated:    " + $tolerated.Count) -ForegroundColor DarkGray
foreach ($t in $tolerated) { Write-Host ("                  " + $t) -ForegroundColor DarkGray }
Write-Host ("  In progress:  " + $inProgress.Count) -ForegroundColor Yellow
foreach ($p in $inProgress) { Write-Host ("                  " + $p) -ForegroundColor Yellow }
Write-Host ("  Hard red:     " + $hardRed.Count) -ForegroundColor Red
foreach ($r in $hardRed) { Write-Host ("                  " + $r) -ForegroundColor Red }

Write-Host ""
if ($hardRed.Count -gt 0) {
    Write-Host ("BLOCKED: " + $hardRed.Count + " red check(s) on the previous commit.") -ForegroundColor Red
    Write-Host "Fix those before shipping the next -rN, or add to TOLERATE_FAILURE if intentional." -ForegroundColor Red
    exit 1
}
if ($inProgress.Count -gt 0) {
    Write-Host ("BLOCKED: " + $inProgress.Count + " check(s) still running. Wait + retry.") -ForegroundColor Yellow
    exit 2
}
Write-Host "OK - safe to ship the next -rN." -ForegroundColor Green
exit 0
