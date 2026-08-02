# verify-last-commit-ci.ps1 - hard precondition for ship-rolling-candidate.
# See .githooks/pre-push + .claude/skills/ship-rolling-candidate/SKILL.md
# for context. Bails (exit 1/2/3) if previous commit CI is not green.

param(
    [string]$Repo,
    [string]$IgnoreSkipped,
    [string]$TolerateFailure,
    [string]$Commit
)

if (-not $Repo) { $Repo = $env:REPO; if (-not $Repo) { $Repo = "PavelLizunov/VPNRouter" } }
if (-not $IgnoreSkipped) { $IgnoreSkipped = $env:IGNORE_SKIPPED; if (-not $IgnoreSkipped) { $IgnoreSkipped = "Build Android APK" } }
if (-not $TolerateFailure) { $TolerateFailure = $env:TOLERATE_FAILURE }
if (-not $Commit) { $Commit = $env:COMMIT; if (-not $Commit) { $Commit = "HEAD" } }

$ErrorActionPreference = "Stop"

$head = (git rev-parse $Commit 2>$null)
if (-not $head -or $LASTEXITCODE -ne 0) {
    Write-Host "INFO: could not resolve commit reference. Allowing." -ForegroundColor Yellow
    exit 0
}
$head = $head.Trim()
Write-Host "Verifying CI for $Commit : $head" -ForegroundColor Cyan

gh auth status 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: gh CLI not authenticated." -ForegroundColor Red
    exit 3
}

$apiPath = "repos/$Repo/commits/$head/check-runs?per_page=30"
$json = gh api $apiPath 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: gh api failed." -ForegroundColor Red
    Write-Host $json
    exit 3
}

$data = $json | ConvertFrom-Json
$checks = $data.check_runs

if (-not $checks -or $checks.Count -eq 0) {
    Write-Host "WARN: no check-runs yet. Wait 30s and retry." -ForegroundColor Yellow
    exit 2
}

$skipOk = @{}
foreach ($n in $IgnoreSkipped.Split(',')) {
    $t = $n.Trim()
    if ($t) { $skipOk[$t] = $true }
}
$failOk = @{}
if ($TolerateFailure) {
    foreach ($n in $TolerateFailure.Split(',')) {
        $t = $n.Trim()
        if ($t) { $failOk[$t] = $true }
    }
}

# audit P2-3 (2026-06-25): TOLERATE_FAILURE is allowlist-restricted + audited.
# Pre-fix it could silently wave through ANY named red check with no trace (the
# r24..r29 red-X streak the hook was built to prevent). Now: only known-flaky
# checks, only with the corroborating sentinel, only with a logged reason.
if ($failOk.Count -gt 0) {
    $allowedTolerate = @('test')   # Linux MVM characterization hash-drift only
    $repoRoot = (git rev-parse --show-toplevel 2>$null)
    if ($repoRoot) { $repoRoot = $repoRoot.Trim() }
    $sentinel = if ($repoRoot) { Join-Path $repoRoot '.git-suggested-hash-bump.txt' } else { $null }
    $reason = $env:TOLERATE_REASON
    foreach ($k in @($failOk.Keys)) {
        if ($allowedTolerate -notcontains $k) {
            Write-Host "REFUSED TOLERATE_FAILURE='$k': not in allowlist ($($allowedTolerate -join ',')). Fix the failure." -ForegroundColor Red
            $failOk.Remove($k)
        }
        elseif (-not ($sentinel -and (Test-Path $sentinel))) {
            Write-Host "REFUSED TOLERATE_FAILURE='$k': requires the Linux hash-drift sentinel (.git-suggested-hash-bump.txt)." -ForegroundColor Red
            $failOk.Remove($k)
        }
        elseif ([string]::IsNullOrWhiteSpace($reason)) {
            Write-Host "REFUSED TOLERATE_FAILURE='$k': set `$env:TOLERATE_REASON='<why>' to audit the waiver." -ForegroundColor Red
            $failOk.Remove($k)
        }
    }
    if ($failOk.Count -gt 0 -and $repoRoot) {
        $log = Join-Path $repoRoot ".ci-tolerated-$($head.Substring(0,8)).txt"
        "commit=$head tolerated=$($failOk.Keys -join ',') reason=$reason" | Out-File -FilePath $log -Encoding utf8
        Write-Host "::warning::CI failure TOLERATED for [$($failOk.Keys -join ',')]: $reason (audit log: $log)" -ForegroundColor Yellow
    }
}

$hardRed = New-Object System.Collections.ArrayList
$inProgress = New-Object System.Collections.ArrayList
$tolerated = New-Object System.Collections.ArrayList
$green = 0
foreach ($c in $checks) {
    $name = $c.name
    $conclusion = $c.conclusion
    $status = $c.status

    if ($status -ne "completed") {
        [void]$inProgress.Add("$name [$status]")
        continue
    }

    if ($conclusion -eq "success") {
        $green++
    }
    elseif ($conclusion -eq "skipped") {
        if ($skipOk.ContainsKey($name)) {
            [void]$tolerated.Add("$name [skipped, expected]")
        } else {
            # audit P2-3: an UNEXPECTED skipped (path filter narrowed / `if:`
            # flipped) on a check that should have run must not silently read as
            # green. Surfaced loudly (not hard-red, which would break legit
            # conditional jobs).
            [void]$tolerated.Add("$name [skipped, UNEXPECTED - confirm it should skip]")
            Write-Host "::warning::Unexpected skipped check '$name' on $head - confirm it was meant to skip." -ForegroundColor Yellow
        }
    }
    elseif ($conclusion -eq "failure") {
        if ($failOk.ContainsKey($name)) {
            [void]$tolerated.Add("$name [failure, tolerated]")
        } elseif ($name -eq "publish") {
            $completedAt = if ($c.completed_at) { [DateTime]$c.completed_at } else { [DateTime]::MinValue }
            $newerSuccess = $checks | Where-Object {
                $_.name -eq $name -and
                $_.conclusion -eq "success" -and
                $_.completed_at -and
                ([DateTime]$_.completed_at) -gt $completedAt
            } | Select-Object -First 1

            if ($newerSuccess) {
                [void]$tolerated.Add("$name [failure, superseded by later success]")
            } else {
                [void]$hardRed.Add("$name $($c.html_url)")
            }
        } else {
            [void]$hardRed.Add("$name $($c.html_url)")
        }
    }
    elseif ($conclusion -eq "cancelled") {
        [void]$tolerated.Add("$name [cancelled]")
    }
    else {
        [void]$hardRed.Add("$name [$conclusion] $($c.html_url)")
    }
}

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Green:        $green" -ForegroundColor Green
Write-Host "  Tolerated:    $($tolerated.Count)" -ForegroundColor DarkGray
foreach ($t in $tolerated) { Write-Host "                  $t" -ForegroundColor DarkGray }
Write-Host "  In progress:  $($inProgress.Count)" -ForegroundColor Yellow
foreach ($p in $inProgress) { Write-Host "                  $p" -ForegroundColor Yellow }
Write-Host "  Hard red:     $($hardRed.Count)" -ForegroundColor Red
foreach ($r in $hardRed) { Write-Host "                  $r" -ForegroundColor Red }

Write-Host ""
if ($hardRed.Count -gt 0) {
    Write-Host "BLOCKED: $($hardRed.Count) red check(s) on the previous commit." -ForegroundColor Red
    Write-Host "Fix those before shipping next candidate, or use TOLERATE_FAILURE env var." -ForegroundColor Red
    exit 1
}
if ($inProgress.Count -gt 0) {
    Write-Host "BLOCKED: $($inProgress.Count) check(s) still running. Wait + retry." -ForegroundColor Yellow
    exit 2
}
Write-Host "OK: safe to ship the next candidate." -ForegroundColor Green
exit 0
