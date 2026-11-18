$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')

function New-RollbackProcess([Diagnostics.ProcessStartInfo]$info) {
    return [Diagnostics.Process]::Start($info)
}

function Start-RestoredApplication([Diagnostics.ProcessStartInfo]$info, [bool]$trampoline) {
    $process = New-RollbackProcess $info
    if (!$process) { throw 'Could not launch the restored VPNRouter application.' }
    try {
        if ($trampoline) {
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) {
                $failure = [InvalidOperationException]::new("Rollback GUI could not launch App (exit $($process.ExitCode)).")
                $failure.Data['TrampolineExit'] = $process.ExitCode
                throw $failure
            }
            # The integrity-aware Go launcher can also exit zero after repairing.
            # Its per-launch log records the PID only after App's CreateProcess succeeds.
            $log = [IO.Path]::Combine($env:TEMP, 'vpnrouter-trampoline.log')
            if ([IO.File]::Exists($log) -and [IO.File]::ReadAllText($log) -cnotmatch '(?m)^\[[^\r\n]+\] App\.exe launched pid=[1-9][0-9]*\r?$') {
                throw 'Rollback GUI exited without confirming the App launch.'
            }
            if (![IO.File]::Exists([IO.Path]::Combine($info.WorkingDirectory, 'VPNRouter.App.exe'))) {
                throw 'Rollback GUI exited without an App to launch.'
            }
        }
    } finally { $process.Dispose() }
}

function Invoke-RollbackRelaunch([string]$applicationDir) {
    $trampoline = $env:VPNROUTER_ROLLBACK_TRAMPOLINE -eq '1'
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
        Start-RestoredApplication $info $trampoline
    } catch {
        if ([IO.File]::Exists($savedMarker)) { [IO.File]::Move($savedMarker, $marker) }
        throw
    }
    try { [IO.File]::Delete($savedMarker) } catch { }
}

try { Invoke-RollbackRelaunch $env:VPNROUTER_ROLLBACK_APP } catch {
    $code = $_.Exception.Data['TrampolineExit']
    if ($code -in @(2, 3, 4)) { exit $code }
    throw
}
