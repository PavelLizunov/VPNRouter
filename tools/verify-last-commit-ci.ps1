# verify-last-commit-ci.ps1 - hard precondition for ship-rolling-candidate.
# See .githooks/pre-push + .claude/skills/ship-rolling-candidate/SKILL.md
# for context. Bails (exit 1/2/3) if previous commit CI is not green.

param(
    [string]$Repo,
    [string]$IgnoreSkipped,
    [string]$TolerateFailure,
    [string]$Commit,
    [string]$RequiredSuccess,
    [string]$RequiredWorkflows,
    [switch]$Strict
)

if (-not $Repo) { $Repo = $env:REPO; if (-not $Repo) { $Repo = "PavelLizunov/VPNRouter" } }
if ($Strict) {
    if (-not $IgnoreSkipped) { $IgnoreSkipped = 'characterization-windows' }
    if (-not $RequiredSuccess) {
        $RequiredSuccess = 'publish=1,verify=1,test-update=1,test=1,go-test-windows=1,characterization-windows=1'
    }
    if (-not $RequiredWorkflows) {
        $RequiredWorkflows = 'Build macOS DMG,Build Android APK,Build Linux AppImage + .deb,Publish APT Repository,Verify Release Integrity,Auto-Update Integration Test (Windows)'
    }
    # A release gate must not inherit developer waivers from the caller.
    $TolerateFailure = $null
}
else {
    if (-not $IgnoreSkipped) { $IgnoreSkipped = $env:IGNORE_SKIPPED; if (-not $IgnoreSkipped) { $IgnoreSkipped = "Build Android APK" } }
    if (-not $TolerateFailure) { $TolerateFailure = $env:TOLERATE_FAILURE }
}
if (-not $Commit) { $Commit = $env:COMMIT; if (-not $Commit) { $Commit = "HEAD" } }

$ErrorActionPreference = "Stop"

$previousResolveErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $head = (git rev-parse --verify "$Commit^{commit}" 2>$null)
    $resolveExitCode = $LASTEXITCODE
}
finally { $ErrorActionPreference = $previousResolveErrorActionPreference }
if (-not $head -or $resolveExitCode -ne 0) {
    if ($Strict) {
        Write-Host "ERROR: could not resolve commit reference." -ForegroundColor Red
        exit 3
    }
    Write-Host "INFO: could not resolve commit reference. Allowing." -ForegroundColor Yellow
    exit 0
}
$head = $head.Trim()
Write-Host "Verifying CI for $Commit : $head" -ForegroundColor Cyan

$apiPath = "repos/$Repo/commits/$head/check-runs?per_page=30"
$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $json = gh api $apiPath 2>&1
    $apiExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($apiExitCode -ne 0) {
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

$workflowRuns = @()
if ($Strict) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $workflowJson = gh api --method GET "repos/$Repo/actions/runs" -f "head_sha=$head" -F 'per_page=100' 2>&1
        $workflowApiExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
    if ($workflowApiExitCode -ne 0) {
        Write-Host 'ERROR: GitHub Actions workflow query failed.' -ForegroundColor Red
        Write-Host $workflowJson
        exit 3
    }
    $workflowRuns = @(($workflowJson | ConvertFrom-Json).workflow_runs)
}

$requiredGreen = @{}
if ($Strict -and $RequiredSuccess) {
    foreach ($entry in $RequiredSuccess.Split(',')) {
        $trimmed = $entry.Trim()
        if (-not $trimmed) { continue }
        if ($trimmed -notmatch '^(?<name>[^=]+)=(?<count>[1-9][0-9]*)$') {
            Write-Host "ERROR: invalid RequiredSuccess entry '$trimmed'." -ForegroundColor Red
            exit 3
        }
        $requiredGreen[$Matches.name.Trim()] = [int]$Matches.count
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
        } elseif ($Strict) {
            [void]$hardRed.Add("$name [skipped, unexpected] $($c.html_url)")
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
        if ($Strict) {
            [void]$hardRed.Add("$name $($c.html_url)")
        } elseif ($failOk.ContainsKey($name)) {
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
        if ($Strict) {
            [void]$hardRed.Add("$name [cancelled] $($c.html_url)")
        } else {
            [void]$tolerated.Add("$name [cancelled, superseded by success]")
        }
    }
    else {
        [void]$hardRed.Add("$name [$conclusion] $($c.html_url)")
    }
}


if ($Strict) {
    foreach ($required in $requiredGreen.GetEnumerator()) {
        $observed = @($checks | Where-Object {
            $_.name -eq $required.Key -and
            $_.status -eq 'completed' -and
            $_.conclusion -eq 'success'
        }).Count
        if ($observed -lt $required.Value) {
            [void]$hardRed.Add("$($required.Key) [required green: $($required.Value), observed: $observed]")
        }
    }

    foreach ($workflowName in $RequiredWorkflows.Split(',')) {
        $requiredWorkflow = $workflowName.Trim()
        if (-not $requiredWorkflow) { continue }
        $successfulRun = @($workflowRuns | Where-Object {
            $_.name -eq $requiredWorkflow -and
            $_.head_sha -eq $head -and
            $_.status -eq 'completed' -and
            $_.conclusion -eq 'success'
        }).Count
        if ($successfulRun -lt 1) {
            [void]$hardRed.Add("workflow '$requiredWorkflow' [required successful run missing]")
        }
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
