
[CmdletBinding()]
param(
    [switch]$Purge,

    [switch]$Yes,

    [switch]$Elevated
)

$env:PSModulePath = [IO.Path]::Combine($PSHOME, "Modules")
$ErrorActionPreference = "Continue"   # don't abort on individual cleanup failures

$InstallRoot   = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) "VPNRouter"
$DataRoot      = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "VPNRouter"
$StartMenuDir  = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "Microsoft\Windows\Start Menu\Programs"
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

    $flags = 0
    if ($Purge) { $flags += 1 }
    if ($Yes) { $flags += 2 }
    $bootstrap = @'
$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$params = @{ Elevated = $true }
if (__FLAGS__ -band 1) { $params.Purge = $true }
if (__FLAGS__ -band 2) { $params.Yes = $true }
$response = Invoke-WebRequest -Uri 'https://vpn.ninitux.com/uninstall.ps1' -UseBasicParsing
$body = $response.Content
if ($body -is [byte[]]) { $body = [Text.Encoding]::UTF8.GetString($body) }
& ([ScriptBlock]::Create([string]$body)) @params
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap.Replace('__FLAGS__', [string]$flags)))
    $shell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    Start-Process -FilePath $shell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
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

$app = Join-Path $InstallRoot 'app'
$stop = Join-Path $app 'stop-vpnrouter.ps1'
$maintenance = Join-Path $app 'zapret-maintenance.ps1'
$ps = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
if ([IO.File]::Exists($stop) -and [IO.File]::Exists($maintenance)) {
    & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $stop -InstallDir $InstallRoot -DataDir $DataRoot -RemoveService
    if ($LASTEXITCODE -notin @(0, 10)) { throw 'Could not stop the installed edition safely.' }
    & (Join-Path $app 'VPNRouter.CLI.exe') cleanup
    if ($LASTEXITCODE -ne 0) { Warn 'System cleanup reported errors.' }
} else {
    # Older installations did not ship the ownership helper.
    $svc = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'" -ErrorAction Stop
    $exe = Join-Path $app 'VPNRouter.Service.exe'
    if ($svc -and $svc.PathName -in @(('"' + $exe + '" --service'), ('"' + $exe + '"'))) {
        if ($svc.State -ne 'Stopped') { Stop-Service -Name VPNRouter -ErrorAction Stop }
        & $sc delete VPNRouter
    }
    foreach ($p in Get-Process) {
        try {
            $null = $p.Handle
            $image = $p.MainModule.FileName
            if (($image -and [IO.Path]::GetDirectoryName($image) -eq $app -and
                $p.ProcessName -in @('VPNRouter.App','VPNRouter.CLI','VPNRouter.GUI','VPNRouter.Service','sing-box')) -or
                $image -eq (Join-Path $DataRoot 'bin\sing-box.exe')) { $p.Kill() }
        } catch {} finally { $p.Dispose() }
    }
    $driver = Get-CimInstance Win32_SystemDriver -Filter "Name='mullvad-split-tunnel'" -ErrorAction Stop
    if ($driver -and (($driver.PathName -replace '^\\\?\?\\','').Trim('"') -eq (Join-Path $app 'driver\mullvad-split-tunnel.sys'))) {
        if ($driver.State -ne 'Stopped') { Stop-Service -Name 'mullvad-split-tunnel' -ErrorAction Stop }
        & $sc delete mullvad-split-tunnel
    }
}
if ($Purge) {
    # Web publication embeds the release's own guard for installations predating the helper.
    $embedded = '__VPNROUTER_MAINTENANCE_BASE64__'
    if (!$embedded.StartsWith('__')) {
        $embeddedSource = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($embedded)).TrimStart([char]0xFEFF)
    }
    foreach ($operation in @('PurgeRuntime', 'PurgeData')) {
        if ($embeddedSource) {
            $source = "& {`n" + $embeddedSource + "`n} -Edition VPNRouter -Operation " + $operation
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($source))
            & $ps -NoProfile -NonInteractive -EncodedCommand $encoded
        } elseif ([IO.File]::Exists($maintenance)) {
            & $ps -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $maintenance -Edition VPNRouter -Operation $operation
        } else {
            Warn "Retained $DataRoot and the Zapret runtime. Run the current published uninstaller with -Purge to check ownership."
            break
        }
        if ($LASTEXITCODE -ne 0) { Warn "Retained files for $operation. See the preceding ownership diagnostic." }
    }
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

if (!$Purge -and (Test-Path -LiteralPath $DataRoot)) { Say "Preserved user data at $DataRoot" }

Write-Host ""
Ok "VPNRouter uninstalled"
Write-Host ""
