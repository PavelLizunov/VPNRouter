# post-ship-install-launch.ps1 - Phase 2 of post-ship-mcp-verify skill.
# Downloads the freshly-shipped ZIP from GitHub Release, verifies sha256,
# stops any running VPNRouter, extracts over the dev-VM install dir,
# launches the new binary.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .agents/skills/post-ship-mcp-verify/scripts/post-ship-install-launch.ps1 -Version "2.37.0-r20"
#
# Exit codes:
#   0 - binary launched, PID returned in last line
#   1 - download failed
#   2 - sha256 mismatch (corrupt download / wrong file)
#   3 - install dir missing (run a fresh install once first)
#   4 - launch failed (no PID after 6s)

param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Repo = "PavelLizunov/VPNRouter",
    [string]$InstallDir = "C:\Program Files\VPNRouter\app",
    [string]$DownloadDir = ".r-publish"
)

$ErrorActionPreference = "Stop"

$tag = "v$Version"
$zipName = "VPNRouter-v$Version-win.zip"
$sumName = "$zipName.sha256"

Write-Host "Post-ship install for $tag" -ForegroundColor Cyan

# ---- Phase 2.1: stop any running VPNRouter process ----
$running = Get-Process VPNRouter* -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running VPNRouter processes..." -ForegroundColor Yellow
    $running | ForEach-Object {
        Write-Host "  - $($_.Name) PID $($_.Id)"
        try { $_.Kill() } catch { Write-Host "    (already exited)" -ForegroundColor DarkGray }
    }
    Start-Sleep -Seconds 2
}

# ---- Phase 2.2: download from GitHub release ----
if (-not (Test-Path $DownloadDir)) {
    New-Item -ItemType Directory -Path $DownloadDir -Force | Out-Null
}
Push-Location $DownloadDir
try {
    Write-Host "Downloading $zipName + sha256..." -ForegroundColor Cyan
    gh release download $tag --repo $Repo --pattern $zipName --clobber 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: gh release download failed for $tag." -ForegroundColor Red
        exit 1
    }
    gh release download $tag --repo $Repo --pattern $sumName --clobber 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARN: sha256 sidecar missing; skipping integrity check." -ForegroundColor Yellow
    } else {
        # Verify sha256.
        $expected = (Get-Content $sumName -Raw).Trim().Split(' ')[0]
        $actual = (Get-FileHash $zipName -Algorithm SHA256).Hash.ToLower()
        if ($expected.ToLower() -ne $actual) {
            Write-Host "ERROR: sha256 mismatch." -ForegroundColor Red
            Write-Host "  Expected: $expected"
            Write-Host "  Actual:   $actual"
            exit 2
        }
        Write-Host "sha256 verified: $actual" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}

# ---- Phase 2.3: extract over install dir ----
if (-not (Test-Path $InstallDir)) {
    Write-Host "ERROR: install dir $InstallDir not found." -ForegroundColor Red
    Write-Host "Run a fresh install via install.ps1 once first." -ForegroundColor Yellow
    exit 3
}

$extractTmp = Join-Path $DownloadDir "extract-$Version"
if (Test-Path $extractTmp) {
    Remove-Item $extractTmp -Recurse -Force
}
New-Item -ItemType Directory -Path $extractTmp -Force | Out-Null

Write-Host "Extracting ZIP..." -ForegroundColor Cyan
Expand-Archive -Path (Join-Path $DownloadDir $zipName) -DestinationPath $extractTmp -Force

# The win.zip layout is `app/...` at root. Copy contents of app/ into InstallDir.
$srcApp = Join-Path $extractTmp "app"
if (-not (Test-Path $srcApp)) {
    Write-Host "ERROR: ZIP doesn't contain app/ subdir." -ForegroundColor Red
    Write-Host "Contents:"
    Get-ChildItem $extractTmp | Format-Table
    exit 1
}

Write-Host "Copying $srcApp\* into $InstallDir..." -ForegroundColor Cyan
Copy-Item -Path "$srcApp\*" -Destination $InstallDir -Recurse -Force

# Verify a key file got the new write time.
$appDll = Join-Path $InstallDir "VPNRouter.App.dll"
if (Test-Path $appDll) {
    $age = (Get-Date) - (Get-Item $appDll).LastWriteTime
    if ($age.TotalMinutes -gt 5) {
        Write-Host "WARN: VPNRouter.App.dll write time is $([math]::Round($age.TotalMinutes)) min old. Copy may not have overwritten." -ForegroundColor Yellow
    } else {
        Write-Host "VPNRouter.App.dll updated (~$([math]::Round($age.TotalSeconds)) s ago)." -ForegroundColor Green
    }
}

# ---- Phase 2.4: launch ----
$exe = Join-Path $InstallDir "VPNRouter.GUI.exe"
if (-not (Test-Path $exe)) {
    Write-Host "ERROR: $exe missing after install." -ForegroundColor Red
    exit 4
}

Write-Host "Launching $exe..." -ForegroundColor Cyan
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6

# VPNRouter.GUI.exe is a tiny launcher: it spawns VPNRouter.App.exe as a
# child then exits cleanly. Don't check $proc.Id — by t+6s the launcher
# has already exited (its job done). Check for VPNRouter.App.exe instead
# (the actual long-lived Avalonia GUI process). If that's not running,
# the launcher couldn't bootstrap or App.exe crashed during startup.
$app = Get-Process -Name VPNRouter.App -ErrorAction SilentlyContinue
if (-not $app) {
    Write-Host "ERROR: VPNRouter.App.exe not running 6s after launching GUI.exe." -ForegroundColor Red
    Write-Host "       Launcher PID was $($proc.Id) (already exited as expected)." -ForegroundColor Red
    Write-Host "       Check %ProgramData%\VPNRouter\logs\ for startup crash trace." -ForegroundColor Red
    exit 4
}

# Multiple App.exe instances would be unusual but valid (e.g. user already
# had one running). Report the first one we see — it's the one our launch
# spawned if it was the only one before.
$appProc = $app | Select-Object -First 1
Write-Host ""
Write-Host "LAUNCHED: VPNRouter.App.exe PID $($appProc.Id) (launcher GUI.exe PID $($proc.Id) already exited)." -ForegroundColor Green
Write-Host "Next: take screenshot via mcp__vpnrouter-test__screenshot to confirm window." -ForegroundColor Cyan
exit 0
