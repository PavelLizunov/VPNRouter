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

# v2.31.8-r5 — silence Invoke-WebRequest progress bar.
# PowerShell 5.1 (Windows PowerShell, default on Win10/11 LTSC) renders
# Invoke-WebRequest progress as a *console banner* by default, which on
# large downloads (Windows install ZIP is ~60 MB) slows the transfer
# 10-100x because every byte triggers a banner repaint. User-reported
# 2026-05-05: install.ps1 hung at "Writing request stream...
# (Number of bytes written: 50707483)" mid-download. Setting this to
# SilentlyContinue restores native socket throughput. Standard fix
# documented in Microsoft's IWR perf KB.
$ProgressPreference = 'SilentlyContinue'

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

    # IMPORTANT: download via -OutFile, NOT via .Content / WriteAllText.
    #
    # On Windows PowerShell 5.1, Invoke-WebRequest returns .Content as
    # Byte[] (not string) when the response Content-Type isn't recognized
    # as text. github.io / Pages occasionally serves install.ps1 with
    # application/octet-stream depending on edge cache state, which
    # triggers this. WriteAllText(path, byte[]) implicitly stringifies
    # the array via [string]::Join(' ', $bytes), so the saved file
    # literally contains "35 32 86 80 78 82..." (each byte as decimal),
    # then the elevated shell tries to parse those numbers as PowerShell
    # tokens and emits a wall of "Unexpected token '32'" errors.
    #
    # -OutFile writes raw response bytes to disk regardless of the
    # detected MIME type, so the saved file is byte-identical to what
    # the server sent. Same fix applied to the .sha256 sidecar earlier.
    $bootstrap = @"
`$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
`$tmp = Join-Path `$env:TEMP 'vpnrouter-install.ps1'
Invoke-WebRequest -Uri 'https://vpn.ninitux.com/install.ps1' -OutFile `$tmp -UseBasicParsing -ErrorAction Stop
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

# Verify SHA256.
#
# Quirk: Windows PowerShell 5.1 returns Invoke-WebRequest .Content as
# Byte[] for non-text responses (and GitHub serves .sha256 as
# application/octet-stream), so calling .Trim() on .Content fails with
# "Method 'Trim' does not exist on [System.Byte]". Workaround: download
# to a temp file and read as text. Works on both PS 5.1 and PS 7+.
if ($shaAsset) {
    Say "Verifying SHA256..."
    $shaTmp = Join-Path $env:TEMP "vpnr-install-sha.txt"
    Invoke-WebRequest -Uri $shaAsset.browser_download_url -OutFile $shaTmp -UseBasicParsing
    $expectedSha = (Get-Content -Raw $shaTmp).Trim().Split()[0].ToLower()
    Remove-Item $shaTmp -Force -ErrorAction SilentlyContinue
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

# == Snapshot Service state BEFORE killing processes =====================
# v2.31.8 — bug fix: previously the Stop-Process loop below killed the
# VPNRouter.Service.exe process directly, which made the Service Control
# Manager report Status=Stopped on the next Get-Service call. The
# `if ($svc.Status -eq 'Running')` branch then never matched, $svcWasRunning
# stayed false, and the post-install Start-Service block at the bottom was
# skipped — leaving the user's Service in Stopped state after every
# upgrade. Symptom: «службу пришлось руками запускать после обновления».
# Capture the running state BEFORE we touch anything else.
$svc = Get-Service -Name VPNRouter -ErrorAction SilentlyContinue
$svcWasRunning = ($svc -and $svc.Status -eq 'Running')

# == Stop service GRACEFULLY first if it was running =====================
# Stop-Service waits for the SCM-tracked stop transition so when we
# subsequently kill any leftover VPNRouter.Service.exe process (defensive)
# the SCM already knows the service is intentionally Stopped, not crashed.
if ($svcWasRunning) {
    Say "Stopping VPNRouter service (was running) before file replacement..."
    Stop-Service -Name VPNRouter -Force -ErrorAction SilentlyContinue
    # Wait up to 10 s for SCM to confirm Stopped state.
    $tries = 0
    while ($tries -lt 20) {
        $svc.Refresh()
        if ($svc.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
        $tries++
    }
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

# == Windows Defender exclusions (interim, until code-signing — Task #132) =====
# The binaries (VPNRouter.App.exe, sing-box.exe, the DNS-tunnel sidecar) are
# UNSIGNED and do TUN + process-scan + firewall + DNS-tunnelling — textbook AV
# heuristic targets. Without an exclusion a boot/scheduled scan can quarantine
# them, and the user sees "VPNRouter disappeared after a reboot".
#
# CRITICAL: Tamper Protection (ON by default on Win10 1903+/Win11) silently
# blocks Add-MpPreference even when elevated. The old code used
# -ErrorAction SilentlyContinue and THEN unconditionally printed "added" — so it
# lied when the exclusion never took. Now we VERIFY the exclusion stuck and, if
# not, tell the user exactly what to do by hand.
$exclOk = $false
try {
    Add-MpPreference -ExclusionPath $InstallRoot -ErrorAction SilentlyContinue
    Add-MpPreference -ExclusionPath $DataRoot    -ErrorAction SilentlyContinue
    $paths = @()
    try { $paths = (Get-MpPreference -ErrorAction SilentlyContinue).ExclusionPath } catch {}
    $exclOk = ($paths -contains $InstallRoot) -and ($paths -contains $DataRoot)
} catch {}

if ($exclOk) {
    Ok "Defender exclusions verified ($InstallRoot, $DataRoot)"
} else {
    # Figure out WHY, so the message is actionable.
    $tp = $null
    try { $tp = (Get-MpComputerStatus -ErrorAction SilentlyContinue).IsTamperProtected } catch {}
    $thirdParty = @()
    try {
        $thirdParty = (Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
            Where-Object { $_.displayName -and $_.displayName -notmatch 'Windows Defender' } | Select-Object -ExpandProperty displayName)
    } catch {}

    Warn "Could NOT confirm a Defender exclusion for VPNRouter."
    if ($tp -eq $true) { Warn "  Reason: Windows Tamper Protection is ON — it blocks scripted exclusions." }
    if ($thirdParty) { Warn "  Detected third-party antivirus: $($thirdParty -join ', ') (Defender exclusions do not apply to it)." }
    Warn "  => VPNRouter is UNSIGNED; an antivirus may delete it on a reboot scan."
    Warn "  => Add these two paths to your antivirus / Windows Security EXCLUSIONS manually:"
    Warn "       $InstallRoot"
    Warn "       $DataRoot"
    Warn "     Windows Security > Virus & threat protection > Manage settings > Exclusions > Add > Folder."
}

# == Data directory ACL (SEC-2) ==========================================
# Tighten $DataRoot: SYSTEM + Administrators FullControl, installing user
# Modify, drop inherited BUILTIN\Users read. Well-known SIDs are used so
# the script works on any Windows locale. Idempotent.
Say "Restricting data directory ACL ($DataRoot)..."
try {
    if (-not (Test-Path $DataRoot)) {
        New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    }
    $acl = Get-Acl $DataRoot
    $acl.SetAccessRuleProtection($true, $true)

    $inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit,ObjectInherit"
    $noProp  = [System.Security.AccessControl.PropagationFlags]::None

    # Well-known SIDs (locale-independent, unlike "Users"/"Administrators").
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $adminsSid = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $usersSid  = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)

    $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $systemSid,"FullControl",$inherit,$noProp,"Allow")))
    $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $adminsSid,"FullControl",$inherit,$noProp,"Allow")))
    try {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $me,"Modify",$inherit,$noProp,"Allow")))
    } catch { }

    # RemoveAccessRuleAll drops EVERY Allow ACE for BUILTIN\Users.
    $acl.RemoveAccessRuleAll((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $usersSid,"ReadAndExecute",$inherit,$noProp,"Allow"))) | Out-Null

    Set-Acl $DataRoot $acl
    Ok "Data directory ACL restricted (SYSTEM + Administrators FullControl, Users read removed)"
} catch {
    Warn "Could not restrict data directory ACL: $_"
    Warn "  => On a shared box, local users may still read $DataRoot (config / logs)."
    Warn "     Restrict it manually: icacls `"$DataRoot`" /inheritance:r /grant:r SYSTEM:F Administrators:F"
}

# Clean up downloaded ZIP (cache in %TEMP% is fine to keep, but tidy)
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# == Start Menu shortcut =================================================
# v2.31.9-r1: TargetPath now points at VPNRouter.GUI.exe (the Go stub
# trampoline) instead of VPNRouter.App.exe directly. Every shortcut-
# initiated launch now passes through the trampoline's integrity check
# + auto-repair before reaching App.exe. IconLocation still references
# App.exe — the Go stub doesn't carry an icon resource and we want
# Start Menu to show the penguin icon, not a generic app glyph.
Say "Creating Start Menu shortcut..."
$lnkPath = Join-Path $StartMenuDir "VPNRouter.lnk"
$wsh = New-Object -ComObject WScript.Shell
$lnk = $wsh.CreateShortcut($lnkPath)
$lnk.TargetPath       = Join-Path $AppDir "VPNRouter.GUI.exe"
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
# v2.31.9-r1: launch via the trampoline stub so the integrity check runs
# even on first launch right after install (covers the rare case where
# the just-extracted ZIP somehow lands a mismatched DLL set, plus keeps
# the launch flow uniform with the Start Menu shortcut path).
if (-not $NoLaunch) {
    Say "Launching VPNRouter..."
    Start-Process (Join-Path $AppDir "VPNRouter.GUI.exe")
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
