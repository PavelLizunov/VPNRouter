# VPNRouter uninstaller for Windows.
#
# Invoked via:
#   - "Settings -> Apps -> VPNRouter -> Uninstall" (via UninstallString in
#     HKLM\...\Uninstall\VPNRouter, populated by install.ps1)
#   - Direct: iwr -useb https://vpn.ninitux.com/uninstall.ps1 | iex
#
# Does:
#   1. Self-elevates via UAC if needed.
#   2. Stops VPNRouter.App / sing-box if running.
#   3. Uninstalls the Windows Service if registered.
#   4. Removes C:\Program Files\VPNRouter.
#   5. Removes Start Menu shortcut.
#   6. Removes the Add/Remove Programs registry key.
#   7. LEAVES %ProgramData%\VPNRouter intact by default (user configs,
#      logs, subscription cache). Pass -Purge to wipe that too.
#   8. LEAVES Windows DNS registry hardening alone. VPNRouter.App
#      restores the pre-VPN values via WindowsDnsHardening.Restore() on
#      each shutdown, so if you uninstalled while VPN was running cleanly,
#      your DNS config is already back to normal. If the app crashed,
#      DNS may still be set to our values - fix manually via
#      `ipconfig /flushdns` + `netsh interface ip reset`.

[CmdletBinding()]
param(
    # Also remove user data (config.yaml, logs, profiles cache, subscriptions).
    # Default off - preserving config lets users re-install without
    # reconfiguring servers.
    [switch]$Purge,

    # Skip interactive confirmation (for scripted uninstalls / Add-Remove
    # flow where no terminal is visible to click through).
    [switch]$Yes,

    # Internal: re-invoked from elevated process.
    [switch]$Elevated
)

$ErrorActionPreference = "Continue"   # don't abort on individual cleanup failures

$InstallRoot   = Join-Path $env:ProgramFiles "VPNRouter"
$DataRoot      = Join-Path $env:ProgramData "VPNRouter"
$StartMenuDir  = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
$UninstallKey  = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VPNRouter"

function Say  ($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok   ($msg) { Write-Host "[OK] $msg"  -ForegroundColor Green }
function Warn ($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Err  ($msg) { Write-Host "[FAIL] $msg" -ForegroundColor Red }

# == Self-elevate ========================================================
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    if ($Elevated) {
        Err "Admin rights required. Uninstallation aborted."
        exit 1
    }

    Say "Uninstaller requires admin rights - triggering UAC prompt..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $passThrough = @("-Elevated")
    if ($Purge) { $passThrough += "-Purge" }
    if ($Yes)   { $passThrough += "-Yes" }
    $flagsString = ($passThrough -join ' ')

    # IMPORTANT: download via -OutFile, NOT via .Content / WriteAllText.
    # Symmetric fix to install.ps1 — see that file for the full explanation
    # of the PS 5.1 Byte[] stringification bug. Short version: WriteAllText
    # implicitly calls [string]::Join(' ', $bytes) on a Byte[], producing
    # "35 32 86 80..." in the saved file, which the elevated shell then
    # tries (and fails) to parse as PowerShell tokens.
    $bootstrap = @"
`$ErrorActionPreference='Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
`$tmp = Join-Path `$env:TEMP 'vpnrouter-uninstall.ps1'
Invoke-WebRequest -Uri 'https://vpn.ninitux.com/uninstall.ps1' -OutFile `$tmp -UseBasicParsing -ErrorAction Stop
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

Say "VPNRouter uninstaller running as Administrator"

# == Confirm (skipped with -Yes) =========================================
if (-not $Yes) {
    Write-Host ""
    Write-Host "This will remove VPNRouter:"
    Write-Host "  - $InstallRoot"
    Write-Host "  - Start Menu shortcut"
    Write-Host "  - Add/Remove Programs entry"
    Write-Host "  - Windows Service (if installed)"
    if ($Purge) {
        Write-Host "  - $DataRoot (user config + logs + caches)  [--Purge]"
    } else {
        Write-Host ""
        Write-Host "  User data preserved: $DataRoot" -ForegroundColor Gray
        Write-Host "  (use -Purge to also remove it)" -ForegroundColor Gray
    }
    Write-Host ""
    $answer = Read-Host "Proceed? [y/N]"
    if ($answer -notmatch "^[yY]") {
        Say "Cancelled."
        exit 0
    }
}

# == Stop running processes ==============================================
Say "Stopping any running VPNRouter / sing-box..."
foreach ($name in @("VPNRouter.App", "VPNRouter.CLI", "VPNRouter.Service", "VPNRouter.GUI", "sing-box")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_ | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
    }
}
Start-Sleep -Milliseconds 500

# == Uninstall Windows Service ===========================================
$svc = Get-Service -Name VPNRouter -ErrorAction SilentlyContinue
if ($svc) {
    Say "Uninstalling Windows Service..."
    if ($svc.Status -eq 'Running') {
        Stop-Service -Name VPNRouter -Force -ErrorAction SilentlyContinue
    }
    $cli = Join-Path $InstallRoot "app\VPNRouter.CLI.exe"
    if (Test-Path $cli) {
        & $cli service uninstall 2>&1 | ForEach-Object { Write-Host "    $_" }
    } else {
        # Installer exe missing - fall back to raw sc.exe
        & sc.exe delete VPNRouter 2>&1 | ForEach-Object { Write-Host "    $_" }
    }
}

# == Remove install dir ==================================================
if (Test-Path $InstallRoot) {
    Say "Removing $InstallRoot..."
    try {
        Remove-Item $InstallRoot -Recurse -Force -ErrorAction Stop
        Ok "Removed $InstallRoot"
    } catch {
        Warn "Could not remove $InstallRoot cleanly: $_"
        Warn "A file may still be locked. Re-run uninstaller after reboot."
    }
}

# == Remove Start Menu shortcut ==========================================
$lnkPath = Join-Path $StartMenuDir "VPNRouter.lnk"
if (Test-Path $lnkPath) {
    Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue
    Ok "Removed Start Menu shortcut"
}

# == Remove Add/Remove Programs entry ====================================
if (Test-Path $UninstallKey) {
    Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Ok "Removed Add/Remove Programs entry"
}

# == Remove per-user Explorer context-menu verb (v2.38.0) ================
# The "route through VPN" verb is registered per-user (HKCU) by the app on
# every launch (ShellMenuRegistrar.Register). We run ELEVATED here, so the
# process HKCU is the admin's hive - NOT the user who installed. Resolve the
# interactive console user's SID and clean their hive via HKEY_USERS, with a
# fallback to the current process hive (covers a non-elevated direct run).
# Best-effort: a leftover verb is only a dead menu entry, never fatal.
Say "Removing Explorer context-menu entry..."
try {
    $sids = New-Object System.Collections.Generic.List[string]
    $consoleUser = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
    if ($consoleUser) {
        try {
            $sids.Add((New-Object Security.Principal.NTAccount($consoleUser)
                ).Translate([Security.Principal.SecurityIdentifier]).Value)
        } catch {}
    }
    try { $sids.Add([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) } catch {}

    foreach ($sid in ($sids | Select-Object -Unique)) {
        foreach ($cls in @("exefile", "lnkfile")) {
            $vk = "Registry::HKEY_USERS\$sid\Software\Classes\$cls\shell\VPNRouterRoute"
            if (Test-Path $vk) {
                Remove-Item $vk -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Ok "Removed Explorer context-menu entry (where present)"
} catch {
    Warn "Could not remove context-menu entry: $_"
}

# == Optional: purge user data ===========================================
if ($Purge) {
    if (Test-Path $DataRoot) {
        Say "Purging $DataRoot..."
        try {
            Remove-Item $DataRoot -Recurse -Force -ErrorAction Stop
            Ok "Removed $DataRoot"
        } catch {
            Warn "Could not remove $DataRoot cleanly: $_"
        }
    }
} else {
    if (Test-Path $DataRoot) {
        Say "Preserved user data at $DataRoot"
        Say "(pass -Purge to also remove it)"
    }
}

Write-Host ""
Ok "VPNRouter uninstalled"
Write-Host ""
