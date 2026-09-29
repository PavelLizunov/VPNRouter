
[CmdletBinding()]
param(
    [switch]$Purge,

    [switch]$Yes,

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

Say "Stopping any running VPNRouter / sing-box..."
foreach ($name in @("VPNRouter.App", "VPNRouter.CLI", "VPNRouter.Service", "VPNRouter.GUI", "sing-box")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_ | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
    }
}
Start-Sleep -Milliseconds 500

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
        & sc.exe delete VPNRouter 2>&1 | ForEach-Object { Write-Host "    $_" }
    }
}

$stSvc = Get-Service -Name "mullvad-split-tunnel" -ErrorAction SilentlyContinue
if ($stSvc) {
    Say "Removing split-tunnel driver service (mullvad-split-tunnel)..."
    & sc.exe stop mullvad-split-tunnel 2>&1 | ForEach-Object { Write-Host "    $_" }
    Start-Sleep -Milliseconds 500
    & sc.exe delete mullvad-split-tunnel 2>&1 | ForEach-Object { Write-Host "    $_" }
}

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

$lnkPath = Join-Path $StartMenuDir "VPNRouter.lnk"
if (Test-Path $lnkPath) {
    Remove-Item $lnkPath -Force -ErrorAction SilentlyContinue
    Ok "Removed Start Menu shortcut"
}

if (Test-Path $UninstallKey) {
    Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Ok "Removed Add/Remove Programs entry"
}

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
            foreach ($verb in @("VPNRouterRoute", "VPNRouterUnroute")) {
                $vk = "Registry::HKEY_USERS\$sid\Software\Classes\$cls\shell\$verb"
                if (Test-Path $vk) {
                    Remove-Item $vk -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }
    Ok "Removed Explorer context-menu entry (where present)"
} catch {
    Warn "Could not remove context-menu entry: $_"
}

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
