$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')

function Start-RestoredApplication([Diagnostics.ProcessStartInfo]$info) {
    $process = [Diagnostics.Process]::Start($info)
    if (!$process) { throw 'Could not launch the restored VPNRouter application.' }
    $process.Dispose()
}

function Invoke-RollbackRelaunch([string]$applicationDir) {
    $exe = [IO.Path]::Combine($applicationDir, 'VPNRouter.GUI.exe')
    if (![IO.File]::Exists($exe)) { $exe = [IO.Path]::Combine($applicationDir, 'VPNRouter.App.exe') }
    if (![IO.File]::Exists($exe)) { throw 'The rollback snapshot has no supported launcher.' }

    # A legacy snapshot may include scratch. Verify its private ACL, then isolate this launch from its contents.
    if ($env:VPNROUTER_ROLLBACK_PREFLIGHT -eq '1') {
        $existing = [IO.Path]::Combine($applicationDir, '.repair-temp')
        if ([IO.File]::Exists($existing)) { throw 'Rollback scratch is not a directory.' }
        if ([IO.Directory]::Exists($existing)) { $null = Get-RepairScratch $applicationDir }
        return
    }
    $privateRoot = Get-RepairScratch $applicationDir
    $session = [IO.DirectoryInfo]::new([IO.Path]::Combine($privateRoot, [Guid]::NewGuid().ToString('N')))
    if ($session.Exists -or [IO.File]::Exists($session.FullName)) { throw 'Rollback scratch already exists.' }
    $session.Create()
    Set-RepairEnvironment $session.FullName
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $exe
    $info.WorkingDirectory = $applicationDir
    $info.UseShellExecute = $false
    $marker = [IO.Path]::Combine($applicationDir, '.update-failed')
    $savedMarker = [IO.Path]::Combine($session.FullName, '.update-failed')
    if ([IO.File]::Exists($marker)) { [IO.File]::Move($marker, $savedMarker) }
    try {
        Start-RestoredApplication $info
    } catch {
        if ([IO.File]::Exists($savedMarker)) { [IO.File]::Move($savedMarker, $marker) }
        throw
    }
    try { [IO.File]::Delete($savedMarker) } catch { }
}

Invoke-RollbackRelaunch $env:VPNROUTER_ROLLBACK_APP
