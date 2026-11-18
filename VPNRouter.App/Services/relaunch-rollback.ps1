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
    $privateRoot = Get-RepairScratch $applicationDir
    $session = [IO.DirectoryInfo]::new([IO.Path]::Combine($privateRoot, [Guid]::NewGuid().ToString('N')))
    if ($session.Exists -or [IO.File]::Exists($session.FullName)) { throw 'Rollback scratch already exists.' }
    $session.Create()
    Set-RepairEnvironment $session.FullName
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $exe
    $info.WorkingDirectory = $applicationDir
    $info.UseShellExecute = $false
    [IO.File]::Delete([IO.Path]::Combine($applicationDir, '.update-failed'))
    Start-RestoredApplication $info
}

Invoke-RollbackRelaunch $env:VPNROUTER_ROLLBACK_APP
