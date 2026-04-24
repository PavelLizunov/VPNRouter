# VPNRouter one-liner installer for Windows 10/11 (x64).
#
# Quick install (non-elevated PowerShell - auto-elevates via UAC):
#   iwr -useb https://vpn.ninitux.com/install.ps1 | iex
#
# Or with options:
#   $cmd = 'param($v) iex (iwr -useb https://vpn.ninitux.com/install.ps1)'
#   Just pass an Execute block if you need Version / Prerelease / Service flags -
#   easier to just download + run: .\install.ps1 -Service
#
# What it does:
#   1. Self-elevates via UAC if not already admin.
#   2. Resolves the target version (latest stable by default).
#   3. Downloads the install ZIP + verifies SHA256 against the sidecar.
#   4. Stops any running VPNRouter / sing-box.
#   5. Extracts to C:\Program Files\VPNRouter (keeps existing config.yaml
#      in %ProgramData%\VPNRouter intact - non-destructive upgrades).
#   6. Registers Start Menu shortcut + Add/Remove Programs entry (so
#      "Settings -> Apps" shows VPNRouter and can remove it cleanly).
#   7. Optionally installs + starts the Windows Service (-Service flag).
#   8. Launches the app (skip with -NoLaunch).
#
# This script mirrors the Linux curl | sh flow + the macOS brew cask flow -
# the three platforms now have a symmetric install UX.

[CmdletBinding()]
param(
    # Explicit version to install (e.g. "2.27.2"). Empty = resolve latest.
    [string]$Version = "",

    # Include prereleases (rolling -rN candidates) when resolving latest.
    # Default false: stable only.
    [switch]$Prerelease,

    # Install Windows Service wrapper after file install (requires service
    # running at boot / survives logoff). Not enabled by default - most
    # users run the app interactively.
    [switch]$Service,

    # Skip launching VPNRouter.App.exe at the end of the install.
    [switch]$NoLaunch,

    # Internal: set when re-invoking self from elevated process so we
    # don't elevate-loop. Users should never pass this manually.
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# == Config ==============================================================
$GitHubRepo    = "PavelLizunov/VPNRouter"
$GitHubApi     = "https://api.github.com/repos/$GitHubRepo"
$InstallRoot   = Join-Path $env:ProgramFiles "VPNRouter"
$AppDir        = Join-Path $InstallRoot "app"
$DataRoot      = Join-Path $env:ProgramData "VPNRouter"
$StartMenuDir  = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
$UninstallKey  = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VPNRouter"
$RemoteUninstall = "https://vpn.ninitux.com/uninstall.ps1"

# == Colored logging =====================================================
function Say ($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok  ($msg) { Write-Host "[OK] $msg" -ForegroundColor Green }
function Warn ($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Err  ($msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }

# == Self-elevate via UAC if not admin ===================================
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($Elevated) {
        # We were supposed to be elevated after the recursive call but we
        # aren't. UAC was cancelled.
        Err "Admin rights required. Installation aborted."
        exit 1
    }

    Say "Installation requires admin rights - triggering UAC prompt..."

    # Re-run ourselves via PowerShell -Verb RunAs. Need to re-download
    # the script in the elevated context because `iwr | iex` has no file
    # on disk - the elevated shell needs to fetch it freshly.
    #
    # Pass our flags through so `-Service` etc. survive the elevation.
    $passThrough = @("-Elevated")
    if ($Version)    { $passThrough += "-Version"; $passThrough += $Version }
    if ($Prerelease) { $passThrough += "-Prerelease" }
    if ($Service)    { $passThrough += "-Service" }
    if ($NoLaunch)   { $passThrough += "-NoLaunch" }
    $flagsString = ($passThrough -join ' ')

    $bootstrap = @"
`$p = @{ErrorActionPreference='Stop'}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
`$script = Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -UseBasicParsing -ErrorAction Stop
`$tmp = Join-Path `$env:TEMP 'vpnrouter-install.ps1'
[IO.File]::WriteAllText(`$tmp, `$script.Content)
& `$tmp $flagsString
pause
"@

    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-Command", $bootstrap
    )
    exit 0
}

# From here on: admin rights confirmed.

Say "VPNRouter installer running as Administrator"

# == Resolve target release ==============================================
Say "Querying GitHub for target release..."

if ($Version) {
    $tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
    try {
        $release = Invoke-RestMethod -Uri "$GitHubApi/releases/tags/$tag" -UseBasicParsing
    } catch {
        Err "Release $tag not found on GitHub: $_"
        exit 1
    }
} else {
    $releases = Invoke-RestMethod -Uri "$GitHubApi/releases?per_page=30" -UseBasicParsing
    $filtered = $releases | Where-Object {
        (-not $_.draft) -and
        ($Prerelease -or (-not $_.prerelease))
    }
    $release = $filtered | Select-Object -First 1
    if (-not $release) { Err "No matching release found"; exit 1 }
}

$resolvedVersion = $release.tag_name.TrimStart('v')
Say "Target version: $resolvedVersion ($(if ($release.prerelease) { 'prerelease' } else { 'stable' }))"

# Check if already installed at same version
$currentVersion = (Get-ItemProperty -Path $UninstallKey -Name DisplayVersion -ErrorAction SilentlyContinue).DisplayVersion
if ($currentVersion -eq $resolvedVersion) {
    Say "VPNRouter $resolvedVersion is already installed. Re-installing over existing."
} elseif ($currentVersion) {
    Say "Upgrading VPNRouter from $currentVersion -> $resolvedVersion"
}

# == Pick install ZIP + sha256 sidecar ===================================
$zipAsset = $release.assets | Where-Object {
    $_.name -like "VPNRouter-v*-win.zip" -and $_.name -notlike "*update*"
} | Select-Object -First 1

if (-not $zipAsset) { Err "Full install ZIP not found on release $($release.tag_name)"; exit 1 }

$shaAsset = $release.assets | Where-Object { $_.name -eq "$($zipAsset.name).sha256" } | Select-Object -First 1

# == Download =============================================================
$zipPath = Join-Path $env:TEMP $zipAsset.name
Say "Downloading $($zipAsset.name) ($([math]::Round($zipAsset.size / 1MB, 1)) MB)..."
Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath -UseBasicParsing

# Verify SHA256
if ($shaAsset) {
    Say "Verifying SHA256..."
    $expectedSha = (Invoke-WebRequest -Uri $shaAsset.browser_download_url -UseBasicParsing).Content.Trim().Split()[0].ToLower()
    $actualSha   = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
    if ($actualSha -ne $expectedSha) {
        Err "SHA256 mismatch! Expected $expectedSha, got $actualSha"
        Remove-Item $zipPath -Force
        exit 1
    }
    Ok "SHA256 verified: $actualSha"
} else {
    Warn "No .sha256 sidecar for this release - skipping hash verification"
}

# == Stop running VPNRouter / sing-box ===================================
$stopped = @()
foreach ($name in @("VPNRouter.App", "VPNRouter.CLI", "VPNRouter.Service", "VPNRouter.GUI", "sing-box")) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        try {
            $p | Stop-Process -Force -ErrorAction SilentlyContinue
            $stopped += "$name (PID $($p.Id))"
        } catch {}
    }
}
if ($stopped.Count -gt 0) { Say "Stopped running: $($stopped -join ', ')" }

# == Stop service if installed ===========================================
$svc = Get-Service -Name VPNRouter -ErrorAction SilentlyContinue
$svcWasRunning = $false
if ($svc) {
    if ($svc.Status -eq 'Running') {
        Say "Stopping VPNRouter service..."
        Stop-Service -Name VPNRouter -Force -ErrorAction SilentlyContinue
        $svcWasRunning = $true
    }
}

Start-Sleep -Milliseconds 500

# == Extract to InstallRoot (fresh) ======================================
Say "Installing to $InstallRoot"
if (Test-Path $InstallRoot) {
    # Remove all EXCEPT config files in InstallRoot root (we don't write
    # any - config lives in %ProgramData%\VPNRouter - but be defensive).
    Get-ChildItem $InstallRoot -Force | ForEach-Object {
        try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch {
            Warn "Could not remove $($_.FullName): $_"
        }
    }
} else {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
}

try {
    Expand-Archive -Path $zipPath -DestinationPath $InstallRoot -Force
} catch {
    Err "Extraction failed: $_"
    exit 1
}

if (-not (Test-Path (Join-Path $AppDir "VPNRouter.App.exe"))) {
    Err "Expected VPNRouter.App.exe not found after extraction. ZIP layout may have changed."
    exit 1
}

Ok "Installed $resolvedVersion to $InstallRoot"

# Clean up downloaded ZIP (cache in %TEMP% is fine to keep, but tidy)
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# == Start Menu shortcut =================================================
Say "Creating Start Menu shortcut..."
$lnkPath = Join-Path $StartMenuDir "VPNRouter.lnk"
$wsh = New-Object -ComObject WScript.Shell
$lnk = $wsh.CreateShortcut($lnkPath)
$lnk.TargetPath       = Join-Path $AppDir "VPNRouter.App.exe"
$lnk.WorkingDirectory = $AppDir
$lnk.IconLocation     = "$(Join-Path $AppDir 'VPNRouter.App.exe'),0"
$lnk.Description      = "Virtual Penguin Network - split-tunnel VPN router"
$lnk.Save()

# == Register Add/Remove Programs entry (HKLM Uninstall key) =============
Say "Registering Add/Remove Programs entry..."
if (-not (Test-Path $UninstallKey)) {
    New-Item -Path $UninstallKey -Force | Out-Null
}
$sizeKb = [int]((Get-ChildItem $InstallRoot -Recurse -ErrorAction SilentlyContinue |
                 Measure-Object -Property Length -Sum).Sum / 1KB)

Set-ItemProperty -Path $UninstallKey -Name DisplayName       -Value "VPNRouter"
Set-ItemProperty -Path $UninstallKey -Name DisplayVersion    -Value $resolvedVersion
Set-ItemProperty -Path $UninstallKey -Name Publisher         -Value "NiniTux"
Set-ItemProperty -Path $UninstallKey -Name DisplayIcon       -Value (Join-Path $AppDir "VPNRouter.App.exe")
Set-ItemProperty -Path $UninstallKey -Name InstallLocation   -Value $InstallRoot
Set-ItemProperty -Path $UninstallKey -Name URLInfoAbout      -Value "https://github.com/$GitHubRepo"
Set-ItemProperty -Path $UninstallKey -Name HelpLink          -Value "https://github.com/$GitHubRepo/issues"
Set-ItemProperty -Path $UninstallKey -Name EstimatedSize     -Value $sizeKb -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoModify          -Value 1       -Type DWord
Set-ItemProperty -Path $UninstallKey -Name NoRepair          -Value 1       -Type DWord

# UninstallString drives the "Uninstall" button in Settings -> Apps
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"iwr -useb $RemoteUninstall | iex`""
Set-ItemProperty -Path $UninstallKey -Name UninstallString -Value $uninstallCmd
Set-ItemProperty -Path $UninstallKey -Name QuietUninstallString -Value $uninstallCmd

Ok "Registered in Add/Remove Programs"

# == Optional: Windows Service ===========================================
if ($Service) {
    Say "Installing Windows Service..."
    $cli = Join-Path $AppDir "VPNRouter.CLI.exe"
    & $cli service install 2>&1 | ForEach-Object { Write-Host "    $_" }
    if ($LASTEXITCODE -eq 0) {
        & $cli service start 2>&1 | ForEach-Object { Write-Host "    $_" }
        Ok "Windows Service installed + started"
    } else {
        Warn "Service install exited with code $LASTEXITCODE"
    }
} elseif ($svcWasRunning) {
    # Was running before we started - restart it post-upgrade
    Say "Restarting VPNRouter service (was running pre-upgrade)..."
    Start-Service -Name VPNRouter -ErrorAction SilentlyContinue
}

# == Launch ==============================================================
if (-not $NoLaunch) {
    Say "Launching VPNRouter..."
    Start-Process (Join-Path $AppDir "VPNRouter.App.exe")
}

# == Summary =============================================================
Write-Host ""
Ok "VPNRouter $resolvedVersion installed successfully"
Write-Host ""
Write-Host "  Install dir:   $InstallRoot"
Write-Host "  Start Menu:    $lnkPath"
Write-Host "  Data dir:      $DataRoot"
Write-Host "  Add/Remove:    Settings -> Apps -> 'VPNRouter'"
Write-Host ""
Write-Host "  Upgrade:       iwr -useb https://vpn.ninitux.com/install.ps1 | iex"
Write-Host "  Uninstall:     iwr -useb https://vpn.ninitux.com/uninstall.ps1 | iex"
Write-Host "                 (or Settings -> Apps -> VPNRouter -> Uninstall)"
Write-Host ""
