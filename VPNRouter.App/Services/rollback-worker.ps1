$ErrorActionPreference = 'Stop'

function Write-RollbackStatus([string]$status) {
    [Console]::Out.WriteLine($status)
    [Console]::Out.Flush()
}

function Invoke-DeferredRollback([IO.DirectoryInfo]$application, [string]$mode) {
    $root = $application.Parent
    $backup = [IO.DirectoryInfo]::new([IO.Path]::Combine($root.FullName, 'app.bak'))
    $displaced = [IO.DirectoryInfo]::new([IO.Path]::Combine($root.FullName, 'app.displaced-' + [Guid]::NewGuid().ToString('N')))
    Assert-RepairRoot $root
    Assert-RepairTree $backup
    $env:VPNROUTER_ROLLBACK_TRAMPOLINE = if ($mode -ceq 'TRAMPOLINE') { '1' } else { '0' }
    $env:VPNROUTER_ROLLBACK_PREFLIGHT = '1'
    Invoke-RollbackRelaunch $backup.FullName
    $env:VPNROUTER_ROLLBACK_PREFLIGHT = $null

    $movedLive = $false
    $published = $false
    try {
        if ($application.Exists) {
            Assert-RepairTree $application
            [IO.Directory]::Move($application.FullName, $displaced.FullName)
            $movedLive = $true
        }
        [IO.Directory]::Move($backup.FullName, $application.FullName)
        $published = $true
        Assert-RepairTree ([IO.DirectoryInfo]::new($application.FullName))
        Invoke-RollbackRelaunch $application.FullName
    } catch {
        if ($published) { [IO.Directory]::Move($application.FullName, $backup.FullName) }
        if ($movedLive) {
            Assert-RepairTree $displaced
            [IO.Directory]::Move($displaced.FullName, $application.FullName)
        }
        throw
    }
    try { [IO.File]::Delete([IO.Path]::Combine($root.FullName, 'app.bak.id')) } catch { }
    if ($movedLive) { try { [IO.Directory]::Delete($displaced.FullName, $true) } catch { } }
}

$application = [IO.DirectoryInfo]::new($env:VPNROUTER_ROLLBACK_APP)
$parent = $null
$lock = $null
$ready = $false
try {
    if ($application.Name -ne 'app' -or !$application.Parent) { throw 'Rollback requires a nested installation.' }
    $parent = [Diagnostics.Process]::GetProcessById([int]$env:VPNROUTER_ROLLBACK_PID)
    $null = $parent.Handle
    if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$env:VPNROUTER_ROLLBACK_TICKS) { throw 'Rollback initiator start time changed.' }
    if ($parent.MainModule.FileName -ne $env:VPNROUTER_ROLLBACK_IMAGE) { throw 'Rollback initiator image changed.' }
    try { $lock = Open-RepairInstallLock $application }
    catch [IO.IOException] {
        if (($_.Exception.GetBaseException().HResult -band 0xffff) -in @(11,32,33)) { Write-RollbackStatus 'BUSY'; return }
        throw
    }
    # The initiating App checks the candidate, its historical launcher ABI and the live
    # tree while this handle excludes publishers. EOF cancels without changing files.
    Write-RollbackStatus 'LOCKED'
    $mode = [Console]::In.ReadLine()
    if ($mode -cnotin @('DIRECT','TRAMPOLINE')) { return }
    Write-RollbackStatus 'READY'
    $ready = $true
    if (!$parent.WaitForExit(45000)) { throw 'Timed out waiting for the initiating App.' }
    Invoke-DeferredRollback $application $mode
} catch {
    if ($ready) {
        $diagnostic = [IO.Path]::Combine($application.Parent.FullName, 'rollback-' + [Guid]::NewGuid().ToString('N') + '.log')
        $file = [IO.File]::Open($diagnostic, 'CreateNew', 'Write', 'Read')
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($_.Exception.Message)
            $file.Write($bytes, 0, $bytes.Length)
        } finally { $file.Dispose() }
    } else { Write-RollbackStatus ("REFUSED " + $_.Exception.Message.Replace([char]13, ' ').Replace([char]10, ' ')) }
    exit 1
} finally {
    if ($lock) { $lock.Dispose() }
    if ($parent) { $parent.Dispose() }
}
