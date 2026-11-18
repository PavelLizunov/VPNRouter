# The initiator holds a real file handle and its working directory until explicitly released.
$ErrorActionPreference = 'Stop'
$root = [IO.Directory]::GetParent($PSScriptRoot).Parent.FullName
$source = [IO.Path]::Combine($root, 'VPNRouter.App', 'Services')
$helpers = [IO.Path]::Combine($root, 'packaging', 'windows', 'installer')
$testRoot = [IO.Directory]::CreateDirectory([IO.Path]::Combine([IO.Path]::GetTempPath(), [Guid]::NewGuid().ToString('N'))).FullName
$shell = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$utf8 = [Text.UTF8Encoding]::new($false)
$body = [IO.File]::ReadAllText([IO.Path]::Combine($helpers,'repair-environment.ps1')) + "`n" +
    [IO.File]::ReadAllText([IO.Path]::Combine($source,'relaunch-rollback.ps1')) + "`n" +
    [IO.File]::ReadAllText([IO.Path]::Combine($helpers,'repair-target.ps1')) + "`n" + @'
function Assert-RepairRoot($dir) {}
function Assert-RepairObject($entry,$ancestor) {}
function Get-RepairScratch($dir) { return [IO.Directory]::CreateDirectory([IO.Path]::Combine($dir,'.repair-temp')).FullName }
function Set-RepairEnvironment($dir) { $env:TEMP = Get-RepairScratch $dir }
function Start-RestoredApplication($info,$trampoline) {
    if ($env:VPNROUTER_FIXTURE_RESULT -eq 'failure') { throw 'Rejected test child.' }
    [IO.File]::WriteAllText([IO.Path]::Combine($info.WorkingDirectory,'started.txt'),'inert')
}
'@ + "`n" + [IO.File]::ReadAllText([IO.Path]::Combine($source,'rollback-worker.ps1'))
# On Linux .NET estimates a wall-clock start time separately in each process.
# Use the kernel's stable process birth tick for the Linux fixture only.
$linuxTicks = [IO.File]::Exists('/proc/self/stat')
if ($linuxTicks) {
    $getTicks = @'
function Get-FixtureStartTick($p) {
    $stat = [IO.File]::ReadAllText("/proc/$($p.Id)/stat")
    return [long]$stat.Substring($stat.LastIndexOf(')') + 2).Split(' ')[19]
}
'@
    . ([scriptblock]::Create($getTicks))
    $body = $getTicks + "`n" + $body.Replace('$parent.StartTime.ToUniversalTime().Ticks', '(Get-FixtureStartTick $parent)')
}
function New-FixtureShell([string]$directory) {
    $info = [Diagnostics.ProcessStartInfo]::new($shell)
    $info.WorkingDirectory = $directory
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $info.RedirectStandardOutput = $true
    if ($info.PSObject.Properties['StandardInputEncoding']) { $info.StandardInputEncoding = $utf8 }
    $command = '& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Console]::In.ReadLine()))))'
    $info.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    return $info
}
function Send-Script($p, [string]$text) {
    $p.StandardInput.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)))
    $p.StandardInput.Flush()
}
function Read-Status($p) {
    $read = $p.StandardOutput.ReadLineAsync()
    if (!$read.Wait(10000)) { throw 'Timed out waiting for helper status.' }
    return $read.Result
}
try {
    foreach ($case in 'success','failure','wrong-parent','busy','cancel') {
        $install = [IO.Directory]::CreateDirectory([IO.Path]::Combine($testRoot,$case)).FullName
        $app = [IO.Directory]::CreateDirectory([IO.Path]::Combine($install,'app')).FullName
        $backup = [IO.Directory]::CreateDirectory([IO.Path]::Combine($install,'app.bak')).FullName
        $old = [IO.Path]::Combine($app,'fixture.dll')
        [IO.File]::WriteAllText($old,'current')
        [IO.File]::WriteAllText([IO.Path]::Combine($backup,'VPNRouter.App.exe'),'inert')
        [IO.File]::WriteAllText([IO.Path]::Combine($backup,'.update-failed'),'candidate diagnostic')
        $parent = $worker = $held = $null
        try {
            $parent = [Diagnostics.Process]::Start((New-FixtureShell $app))
            Send-Script $parent '$f=[IO.File]::OpenRead([IO.Path]::Combine([Environment]::CurrentDirectory,"fixture.dll")); [Console]::WriteLine("PINNED"); [Console]::Out.Flush(); $null=[Console]::ReadLine(); $f.Dispose()'
            if ((Read-Status $parent) -ne 'PINNED') { throw 'Initiator did not pin the live file.' }
            $info = New-FixtureShell $testRoot
            $info.EnvironmentVariables['VPNROUTER_ROLLBACK_DEFERRED'] = '1'
            $info.EnvironmentVariables['VPNROUTER_ROLLBACK_APP'] = $app
            $info.EnvironmentVariables['VPNROUTER_ROLLBACK_PID'] = [string]$parent.Id
            $ticks = if ($linuxTicks) { Get-FixtureStartTick $parent } else { $parent.StartTime.ToUniversalTime().Ticks }
            if ($case -eq 'wrong-parent') { $ticks -= 10000000 }
            $info.EnvironmentVariables['VPNROUTER_ROLLBACK_TICKS'] = [string]$ticks
            $info.EnvironmentVariables['VPNROUTER_ROLLBACK_IMAGE'] = $parent.MainModule.FileName
            $info.EnvironmentVariables['VPNROUTER_FIXTURE_RESULT'] = $case
            $lock = [IO.Path]::Combine($install,'.update-backup.lock')
            if ($case -eq 'busy') { $held = [IO.File]::Open($lock,'OpenOrCreate','ReadWrite','None') }
            $worker = [Diagnostics.Process]::Start($info)
            Send-Script $worker $body
            $status = Read-Status $worker
            if ($case -in @('busy','wrong-parent')) {
                $expected = if ($case -eq 'busy') { 'BUSY' } else { 'REFUSED' }
                if (($status -split ' ',2)[0] -ne $expected) { throw "Unexpected worker status: $status ($case)" }
            } else {
                if ($status -ne 'LOCKED') { throw "Worker did not acquire lock: $status ($case)" }
                if ($case -ne 'cancel') {
                    $worker.StandardInput.WriteLine('DIRECT')
                    $worker.StandardInput.Flush()
                    if ((Read-Status $worker) -ne 'READY') { throw 'Worker did not accept handoff.' }
                    $probe = $null
                    try { $probe = [IO.File]::Open($lock,'Open','ReadWrite','None') } catch [IO.IOException] {}
                    if ($probe) { $probe.Dispose(); throw 'Worker released lock while waiting for initiator.' }
                }
            }
            $worker.StandardInput.Close()
            if (![IO.File]::Exists($old) -or ![IO.Directory]::Exists($backup)) { throw 'Worker mutated live files before initiator exit.' }
            $parent.StandardInput.Close()
            if (!$parent.WaitForExit(10000) -or !$worker.WaitForExit(10000)) { throw 'Worker or initiator failed to exit.' }
            if ($case -eq 'success') {
                if ($worker.ExitCode -ne 0 -or ![IO.File]::Exists([IO.Path]::Combine($app,'started.txt')) -or [IO.File]::Exists($old)) { throw 'Rollback handoff failed.' }
            } else {
                if (![IO.File]::Exists($old) -or [IO.File]::ReadAllText([IO.Path]::Combine($backup,'.update-failed')) -ne 'candidate diagnostic') { throw 'Worker lost live tree or candidate diagnostics.' }
                if ($case -eq 'failure' -and ($worker.ExitCode -eq 0 -or [IO.Directory]::GetFiles($install,'rollback-*.log').Count -ne 1)) { throw 'Worker did not report compensated launch failure.' }
            }
        } finally {
            if ($held) { $held.Dispose() }
            foreach ($p in @($parent,$worker)) {
                if ($p) {
                    if (!$p.HasExited) { $p.StandardInput.Close(); $null = $p.WaitForExit(10000) }
                    $p.Dispose()
                }
            }
        }
    }
} finally { [IO.Directory]::Delete($testRoot,$true) }
'Rollback worker: exact parent exit, lock retention, successful launch, compensation, PID reuse refusal, contention and cancellation passed.'
