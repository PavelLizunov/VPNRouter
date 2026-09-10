# verify-last-commit-ci.ps1 - hard precondition for ship-rolling-candidate.
# See .githooks/pre-push + .dsh/skills/ship-rolling-candidate/SKILL.md
# for context. Bails (exit 1/2/3) if previous commit CI is not green.

param(
    [string]$Repo,
    [string]$IgnoreSkipped,
    [string]$TolerateFailure,
    [string]$Commit,
    [string]$RequiredSuccess,
    [string]$RequiredWorkflows,
    [string]$ReleaseTag,
    [switch]$Strict
)

if (-not $Repo) { $Repo = $env:REPO; if (-not $Repo) { $Repo = "PavelLizunov/VPNRouter" } }
if ($Strict) {
    if ($ReleaseTag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$') {
        Write-Host 'ERROR: Strict requires ReleaseTag vX.Y.Z[-rN].' -ForegroundColor Red
        exit 3
    }
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
    Write-Host "ERROR: could not resolve commit reference." -ForegroundColor Red
    exit 3
}
$head = $head.Trim()
Write-Host "Verifying CI for $Commit : $head" -ForegroundColor Cyan

# Explicit pages work with older gh versions, without concatenated JSON or --slurp.
# Reject changing/truncated populations rather than certify partial evidence.
function Get-ApiItems([string]$Path, [string]$Property) {
    $items = New-Object System.Collections.ArrayList
    $ids = @{}
    $total = $null
    for ($page = 1; $page -le 100; $page++) {
        $separator = if ($Path.Contains('?')) { '&' } else { '?' }
        try {
            $previousPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $json = gh api "${Path}${separator}per_page=100&page=$page" 2>&1
                $code = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $previousPreference }
            if ($code -ne 0) { throw 'gh api failed' }
            $data = ($json -join "`n") | ConvertFrom-Json
            if ($null -eq $data.total_count -or $null -eq $data.$Property -or
                [string]$data.total_count -notmatch '^[0-9]+$') { throw 'missing pagination fields' }
            if ($null -eq $total) { $total = [long]$data.total_count }
            if ([long]$data.total_count -ne $total) { throw 'population changed during pagination' }
            $batch = @($data.$Property)
            foreach ($item in $batch) {
                if ([string]$item.id -notmatch '^[1-9][0-9]*$' -or $ids.ContainsKey([string]$item.id)) {
                    throw 'missing or duplicate item id'
                }
                $ids[[string]$item.id] = $true
                [void]$items.Add($item)
            }
            if ($items.Count -gt $total) { throw 'pagination exceeded total_count' }
            if ($items.Count -eq $total) { return $items.ToArray() }
            if ($batch.Count -ne 100) { throw 'truncated API page' }
        }
        catch {
            Write-Host "ERROR: incomplete GitHub evidence ($Property page $page): $_" -ForegroundColor Red
            exit 3
        }
    }
    Write-Host 'ERROR: GitHub pagination limit exceeded.' -ForegroundColor Red
    exit 3
}

$checks = @(Get-ApiItems "repos/$Repo/commits/$head/check-runs?filter=latest" 'check_runs')

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
    $workflowRuns = @(Get-ApiItems "repos/$Repo/actions/runs?head_sha=$head" 'workflow_runs')
}

$requiredGreen = @{}
if ($Strict -and $RequiredSuccess) {
    foreach ($entry in $RequiredSuccess.Split(',')) {
        $trimmed = $entry.Trim()
        if ($trimmed -notmatch '^(?<name>[^=]+)=(?<count>[1-9][0-9]*)$') {
            Write-Host "ERROR: invalid RequiredSuccess entry '$trimmed'." -ForegroundColor Red
            exit 3
        }
        $jobName = $Matches.name.Trim()
        $count = 0
        if (-not [int]::TryParse($Matches.count, [ref]$count) -or $requiredGreen.ContainsKey($jobName)) {
            Write-Host "ERROR: duplicate or invalid RequiredSuccess entry '$trimmed'." -ForegroundColor Red
            exit 3
        }
        $requiredGreen[$jobName] = $count
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
if ($Strict) {
    $canonical = @{
        'Build macOS DMG' = 'build-mac.yml'
        'Build Android APK' = 'build-android.yml'
        'Build Linux AppImage + .deb' = 'build-linux.yml'
        'Publish APT Repository' = 'publish-apt.yml'
        'Verify Release Integrity' = 'verify-release-integrity.yml'
        'Auto-Update Integration Test (Windows)' = 'test-windows-update.yml'
        'dotnet test' = 'test.yml'
    }
    $jobOwners = @{
        'build' = @('build-mac.yml', 'build-android.yml', 'build-linux.yml')
        'publish' = @('publish-apt.yml')
        'verify' = @('verify-release-integrity.yml')
        'test-update' = @('test-windows-update.yml')
        'test' = @('test.yml')
        'go-test-windows' = @('test.yml')
        'characterization-windows' = @('test.yml')
    }
    $wanted = @{}
    foreach ($workflowName in $RequiredWorkflows.Split(',')) {
        $requiredWorkflow = $workflowName.Trim()
        if (-not $canonical.ContainsKey($requiredWorkflow)) {
            Write-Host "ERROR: unknown canonical workflow '$requiredWorkflow'." -ForegroundColor Red
            exit 3
        }
        $wanted[$canonical[$requiredWorkflow]] = $requiredWorkflow
    }
    foreach ($jobName in $requiredGreen.Keys) {
        if (-not $jobOwners.ContainsKey($jobName)) {
            Write-Host "ERROR: unknown canonical job '$jobName'." -ForegroundColor Red
            exit 3
        }
        foreach ($owner in $jobOwners[$jobName]) {
            if (-not $wanted.ContainsKey($owner)) { $wanted[$owner] = $owner }
        }
    }
    $selectedJobs = New-Object System.Collections.ArrayList
    foreach ($file in $wanted.Keys) {
        $requiredWorkflow = $wanted[$file]
        $events = if ($file -in @('publish-apt.yml', 'verify-release-integrity.yml')) {
            @('release', 'workflow_dispatch')
        } elseif ($file -eq 'test-windows-update.yml') {
            @('push', 'workflow_dispatch', 'release')
        } else { @('push', 'workflow_dispatch') }
        $run = $workflowRuns | Where-Object {
            $_.path -ceq ".github/workflows/$file" -and
            $_.head_sha -ceq $head -and $_.head_branch -ceq $ReleaseTag -and
            $_.event -cin $events
        } | Sort-Object @{ Expression = { [long]$_.id }; Descending = $true },
            @{ Expression = { [long]$_.run_attempt }; Descending = $true } | Select-Object -First 1
        if (-not $run) {
            [void]$hardRed.Add("workflow '$requiredWorkflow' [required successful run missing]")
            continue
        }
        if ($run.status -ne 'completed') {
            [void]$inProgress.Add("workflow '$requiredWorkflow' [$($run.status)]")
            continue
        }
        if ($run.conclusion -ne 'success') {
            [void]$hardRed.Add("workflow '$requiredWorkflow' [$($run.conclusion)]")
            continue
        }
        if ([string]$run.run_attempt -notmatch '^[1-9][0-9]*$') {
            Write-Host 'ERROR: selected workflow has no valid run_attempt.' -ForegroundColor Red
            exit 3
        }
        $jobs = @(Get-ApiItems "repos/$Repo/actions/runs/$($run.id)/attempts/$($run.run_attempt)/jobs" 'jobs')
        if ($jobs.Count -eq 0) { [void]$hardRed.Add("workflow '$requiredWorkflow' [no jobs]") }
        foreach ($job in $jobs) {
            if ([string]$job.run_id -cne [string]$run.id -or $job.head_sha -cne $head) {
                Write-Host 'ERROR: job identity does not match selected run.' -ForegroundColor Red
                exit 3
            }
            $job | Add-Member -NotePropertyName CanonicalWorkflow -NotePropertyValue $file
            [void]$selectedJobs.Add($job)
        }
    }
    $checks = @($selectedJobs.ToArray())
}
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
        } else {
            [void]$hardRed.Add("$name $($c.html_url)")
        }
    }
    elseif ($conclusion -eq "cancelled") {
        [void]$hardRed.Add("$name [cancelled] $($c.html_url)")
    }
    else {
        [void]$hardRed.Add("$name [$conclusion] $($c.html_url)")
    }
}


if ($Strict) {
    foreach ($required in $requiredGreen.GetEnumerator()) {
        $observed = @($checks | Where-Object {
            $_.name -ceq $required.Key -and
            $_.CanonicalWorkflow -cin $jobOwners[$required.Key] -and
            $_.status -eq 'completed' -and
            $_.conclusion -eq 'success'
        }).Count
        if ($observed -lt $required.Value) {
            [void]$hardRed.Add("$($required.Key) [required green: $($required.Value), observed: $observed]")
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
if ($green -eq 0) {
    Write-Host 'BLOCKED: no successful checks; skipped or waived checks are not green evidence.' -ForegroundColor Yellow
    exit 2
}
Write-Host 'OK: CI evidence passed (does not authorize a release).' -ForegroundColor Green
exit 0
