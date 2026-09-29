
param(
    [string]$Sha = "",
    [string]$Repo = "PavelLizunov/VPNRouter",
    [int]$MaxWaitSec = 600,
    [int]$PollSec = 30
)

if (-not $Sha) {
    $Sha = (git rev-parse HEAD 2>$null)
}
if (-not $Sha) {
    Write-Host "[watch] No commit SHA — exit." -ForegroundColor Yellow
    exit 0
}

$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    Write-Host "[watch] Not in a repo — exit." -ForegroundColor Yellow
    exit 0
}

Write-Host "[watch] Monitoring CI for $Sha (max ${MaxWaitSec}s, poll every ${PollSec}s)..." -ForegroundColor Cyan

$start = Get-Date
while (((Get-Date) - $start).TotalSeconds -lt $MaxWaitSec) {
    Start-Sleep -Seconds $PollSec
    $json = gh api "repos/$Repo/commits/$Sha/check-runs" 2>$null | ConvertFrom-Json
    if (-not $json) { continue }

    $testRun = $json.check_runs | Where-Object { $_.name -eq "test" } | Select-Object -First 1
    if (-not $testRun -or $testRun.status -ne "completed") { continue }

    if ($testRun.conclusion -eq "success") {
        Write-Host "[watch] test job GREEN on $Sha — no action needed." -ForegroundColor Green
        exit 0
    }

    Write-Host "[watch] test job FAILURE on $Sha — capturing diagnostic..." -ForegroundColor Red

    $jobId = $testRun.id
    $log = gh api "repos/$Repo/actions/jobs/$jobId/logs" 2>$null
    if ($log -match "Actual:\s+([a-f0-9]{64})") {
        $actualHash = $matches[1]
        $suggestionPath = Join-Path $repoRoot ".git-suggested-hash-bump.txt"
        @"
COMMIT: $Sha
JOB:    $($testRun.html_url)
ACTUAL_LINUX_HASH: $actualHash
CAPTURED_AT: $(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssZ')

Next ship should bump VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs
PinnedHashLinux to this value to clear the red `test` check.
"@ | Out-File $suggestionPath -Encoding utf8 -NoNewline

        Write-Host "[watch] Linux hash drift captured: $actualHash" -ForegroundColor Yellow
        Write-Host "[watch] Wrote $suggestionPath — next ship MUST consume this." -ForegroundColor Yellow
        exit 0
    }

    Write-Host "[watch] test failed but no hash-drift pattern matched. URL: $($testRun.html_url)" -ForegroundColor Red
    exit 0
}

Write-Host "[watch] Timeout after ${MaxWaitSec}s without test conclusion. Run manually:" -ForegroundColor Yellow
Write-Host "        pwsh tools/watch-after-push.ps1 -Sha $Sha" -ForegroundColor Yellow
exit 0
