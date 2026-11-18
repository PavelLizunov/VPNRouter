#requires -Version 5.1
# Stop this installation's known executable images. Exit 10 records a running main service
# for the upgrade restart. WinDivert registrations can be shared across installations.
param(
    [Parameter(Mandatory)][string]$InstallDir,
    [string]$DataDir = '',
    [string]$ApplicationDir = '',
    [switch]$RestartService,
    [switch]$InstallService,
    [switch]$RemoveService
)

$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$app = if ($ApplicationDir) { [IO.Path]::GetFullPath($ApplicationDir) } else { Join-Path $InstallDir 'app' }
$serviceExe = Join-Path $app 'VPNRouter.Service.exe'

function Get-InstallProcessImages($appDirectory, $dataDirectory) {
    $appImages = @('VPNRouter.App.exe','VPNRouter.CLI.exe','VPNRouter.GUI.exe','VPNRouter.Service.exe',
        'sing-box.exe','slipstream-client.exe') | ForEach-Object { Join-Path $appDirectory $_ }
    if ($dataDirectory) {
        $appImages += @('bin\sing-box.exe','slipstream\bin\slipstream-client.exe','tg-proxy\python\python.exe') | ForEach-Object { Join-Path $dataDirectory $_ }
    }
    return $appImages
}
$images = Get-InstallProcessImages $app $DataDir

function Test-ProcessImage([string]$path) {
    return ($path -and $path -in $images)
}

function Test-ServiceImage([string]$image, [string]$exe) {
    $quoted = '"' + $exe + '"'
    return ($image -in @($quoted, ($quoted + ' --service')) -or
        ($exe -notmatch '\s' -and $image -in @($exe, ($exe + ' --service'))))
}

function Get-OwnMainService {
    $svc = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'"
    if (!$svc -or !(Test-ServiceImage $svc.PathName $serviceExe)) { return }
    $controller = Get-Service -Name VPNRouter
    try {
        $null = $controller.ServiceHandle
        $current = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'"
        if ($current -and (Test-ServiceImage $current.PathName $serviceExe)) { return $controller }
    } catch { $controller.Dispose(); throw }
    $controller.Dispose()
}

$serviceStopped = $false
if ($InstallService -or $RestartService) {
    if ($InstallService -and !(Get-CimInstance Win32_Service -Filter "Name='VPNRouter'")) {
        & (Join-Path $app 'VPNRouter.CLI.exe') service install
        if ($LASTEXITCODE -ne 0) { throw 'Could not install the VPNRouter service.' }
    }
    $owned = Get-OwnMainService
    if ($owned) {
        try { Start-Service -InputObject $owned } finally { $owned.Dispose() }
    } elseif ($InstallService) {
        throw 'A different or unverifiable VPNRouter service is already registered.'
    }
    exit 0
}

if ($DataDir) {
    $owned = Get-OwnMainService
    if ($owned) {
        try {
            if ($owned.Status -ne 'Stopped') {
                Stop-Service -InputObject $owned
                $owned.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
                $serviceStopped = $true
            }
            if ($RemoveService) {
                $current = Get-CimInstance Win32_Service -Filter "Name='VPNRouter'"
                if ($current -and (Test-ServiceImage $current.PathName $serviceExe)) {
                    & (Join-Path ([Environment]::SystemDirectory) 'sc.exe') delete VPNRouter | Out-Null
                    if ($LASTEXITCODE -notin @(0,1072)) { throw 'Could not delete the verified VPNRouter service.' }
                }
            }
        } finally { $owned.Dispose() }
    }
}

$self = $PID
$targets = Get-CimInstance Win32_Process | Where-Object {
    $_.ProcessId -ne $self -and
    $_.Name -notmatch '^(unins\d*|VPNRouter-Setup.*)\.(exe|tmp)$' -and
    (Test-ProcessImage $_.ExecutablePath)
}

foreach ($p in $targets) {
    Write-Output ("Stopping {0} (PID {1})" -f $p.Name, $p.ProcessId)
    $live = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
    if ($live) {
        try {
            $null = $live.Handle
            if ($live.MainModule.FileName -eq $p.ExecutablePath -and (Test-ProcessImage $p.ExecutablePath)) { $live.Kill() }
        } catch [System.InvalidOperationException] { } finally { $live.Dispose() }
    }
}

if ($targets) { Start-Sleep -Milliseconds 800 }

# The split-tunnel kernel driver that VPNRouter installs from <InstallDir>\app\driver stays loaded after the app is killed and
# locks its .sys file, so the installer could neither delete nor replace it ("DeleteFile: code 5, access denied"). Stop it
# (only when its file lives in the install folder: a Mullvad client or another copy keeps its own driver); it starts on demand.
$driver = Get-CimInstance Win32_SystemDriver -Filter "Name='mullvad-split-tunnel'"
if ($DataDir -and $driver -and $driver.State -ne 'Stopped' -and ((([string]$driver.PathName) -replace '^\\\?\?\\', '').Trim('"') -eq (Join-Path $app 'driver\mullvad-split-tunnel.sys'))) {
    Write-Output 'Stopping the split-tunnel driver'
    Stop-Service -Name 'mullvad-split-tunnel'
    for ($i = 0; $i -lt 20; $i++) {
        if ((Get-CimInstance Win32_SystemDriver -Filter "Name='mullvad-split-tunnel'").State -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
    }
}

if ($serviceStopped) { exit 10 }
exit 0
