#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$InstallDir,
    [Parameter(Mandatory)][string]$ApplicationDir,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][int]$ParentId,
    [Parameter(Mandatory)][long]$ParentStarted,
    [switch]$Fixture,
    [switch]$SkipRelaunch
)

$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
. (Join-Path $PSScriptRoot 'repair-environment.ps1')
$destination = $ApplicationDir
$log = Join-Path $InstallDir 'update.log'
$systemShell = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\powershell.exe')
$stop = Join-Path $PSScriptRoot 'stop-vpnrouter.ps1'
$maintenance = Join-Path $PSScriptRoot 'zapret-maintenance.ps1'
$copy = [IO.Path]::Combine([Environment]::SystemDirectory, 'xcopy.exe')
$state = @{ LiveChanges = $false }

function Invoke-WindowsPublication {
    $restart = $false
    if (!$Fixture) {
        & $systemShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $maintenance -Edition VPNRouter -Operation Prepare
        if ($LASTEXITCODE -ne 0) { throw 'Could not prepare legacy Zapret for upgrade.' }
        & $systemShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $stop -InstallDir $InstallDir -ApplicationDir $destination -DataDir $DataDir
        if ($LASTEXITCODE -notin @(0, 10)) { throw 'Could not stop the installed VPNRouter.' }
        $restart = $LASTEXITCODE -eq 10
    }
    $state.LiveChanges = $true
    $lines = & $copy (Join-Path $Source '*') $destination /E /Y /Q /R /I
    $result = $LASTEXITCODE
    [IO.File]::AppendAllText($log, ($lines -join [Environment]::NewLine) + [Environment]::NewLine + "xcopy exit=$result" + [Environment]::NewLine)
    if ($result -ne 0) { throw "Unable to publish the release (xcopy exit $result)." }
    if (!$Fixture) { Set-RepairEnvironment $destination }
    if (!$Fixture -and [IO.File]::Exists((Join-Path $Source 'zapret-maintenance.ps1'))) {
        $process = Start-Process -FilePath (Join-Path $destination 'VPNRouter.App.exe') -ArgumentList '--migrate-zapret' -WorkingDirectory $destination -Wait -PassThru
        try { if ($process.ExitCode -notin @(0, 2)) { throw 'Could not restore Zapret configuration.' } }
        finally { $process.Dispose() }
    }
    if ($restart) {
        & $systemShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $stop -InstallDir $InstallDir -ApplicationDir $destination -DataDir $DataDir -RestartService
        if ($LASTEXITCODE -ne 0) { throw 'Could not restart the installed VPNRouter service.' }
    }
    if (!$SkipRelaunch) {
        Start-Process -FilePath (Join-Path $destination 'VPNRouter.GUI.exe') -WorkingDirectory $destination
    }
}

try {
    $lockPath = Join-Path $InstallDir '.update-backup.lock'
    if ([IO.File]::Exists($lockPath) -and ([IO.File]::GetAttributes($lockPath) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Update lock is redirected.'
    }
    $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    [IO.File]::WriteAllText($log, "Windows update publisher started" + [Environment]::NewLine)
    $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if ($parent) {
        try {
            try { $waitForParent = $parent.StartTime.ToUniversalTime().Ticks -eq $ParentStarted }
            catch [InvalidOperationException] { $waitForParent = $false }
            if ($waitForParent -and !$parent.WaitForExit(45000)) {
                throw 'Timed out waiting for the updating process to exit.'
            }
        } finally { $parent.Dispose() }
    }
    Invoke-WindowsPublication
    [IO.File]::AppendAllText($log, 'helper done' + [Environment]::NewLine)
} catch {
    if ($lock) { [IO.File]::AppendAllText($log, $_.Exception.Message + [Environment]::NewLine) }
    if ($state.LiveChanges) { [IO.File]::WriteAllText((Join-Path $destination '.update-failed'), 'Windows update publication failed.') }
    exit 1
} finally {
    if ($lock) { $lock.Dispose() }
}
