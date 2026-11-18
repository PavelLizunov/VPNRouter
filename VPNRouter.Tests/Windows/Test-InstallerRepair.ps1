# Exercise the actual targeted copy and post-repair branches against an ordinary directory tree.
$ErrorActionPreference = 'Stop'
# The production installer loads Windows PowerShell modules from its own runtime.
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$installer = Join-Path $repo 'packaging/windows/install.ps1'
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in 'New-DefaultInstallRoot','Open-InstallerLock','Get-DefaultApplication','Assert-PublicInstallTarget','Complete-TargetRepair','Assert-TargetRepairCopy','Open-TargetFailureMarker') {
    $entry = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    . ([scriptblock]::Create($entry.Extent.Text))
}
$sourceText = $ast.Extent.Text.Replace("`r`n","`n")
function Get-InstallerSlice($start,$end) {
    $from = $sourceText.IndexOf($start,[StringComparison]::Ordinal)
    $to = $sourceText.IndexOf($end,$from,[StringComparison]::Ordinal)
    return $sourceText.Substring($from, $to - $from)
}
# Run the real elevation return path in a child shell. The fake elevated installer only supplies an exit code.
$tail = Get-InstallerSlice '$elevation = Start-Process' ("`n}" + "`n")
foreach ($repairMode in $false,$true) {
    $fixture = @'
function Start-Process {
    $result = [pscustomobject]@{ ExitCode = 23 }
    $result | Add-Member ScriptMethod WaitForExit { $script:waited = $true }
    $result | Add-Member ScriptMethod Dispose { if ($script:waited -ne $Repair) { throw 'Wrong installer wait mode.' } }
    return $result
}
$script:waited = $false
'@
    $fixture += "`n" + '$Repair = $' + $repairMode.ToString().ToLowerInvariant() + "`n" + $tail
    $shell = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $command = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fixture))
    & $shell -NoProfile -NonInteractive -EncodedCommand $command
    $expected = if ($repairMode) { 23 } else { 0 }
    if ($LASTEXITCODE -ne $expected) { throw 'Installer did not return the expected elevation result.' }
}
$acquire = [scriptblock]::Create((Get-InstallerSlice '$repairLock = Open-InstallerLock' '# Capture the old service'))
$wipe = [scriptblock]::Create((Get-InstallerSlice 'if (!$Repair) {' ('try {' + "`n" + '    if ($RepairApplicationDir')))
$copy = [scriptblock]::Create((Get-InstallerSlice '    if ($RepairApplicationDir -or $AppDir -eq $InstallRoot)' '} catch {'))
$complete = $ast.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Clauses[0].Item1.Extent.Text -eq '$Repair' -and $n.Extent.Text.Contains("'VPNRouter.CLI.exe') doctor") }, $true)
$complete = [scriptblock]::Create($complete.Extent.Text.Replace('& (Join-Path ', 'Invoke-FixtureDoctor (Join-Path '))
$transaction = $ast.Find({ param($n) $n -is [Management.Automation.Language.TryStatementAst] -and
    $n.Finally -and $n.Finally.Extent.Text.Contains('$repairLock.Dispose()') }, $true)
$ordered = -1
foreach ($token in '$repairLock = Open-InstallerLock','-Operation Prepare','Get-ChildItem -LiteralPath $InstallRoot -Force | ForEach-Object','Copy-Item -Destination $InstallRoot','Complete-TargetRepair $AppDir') {
    $ordered = $transaction.Body.Extent.Text.IndexOf($token, $ordered + 1)
    if ($ordered -lt 0) { throw "Installer transaction does not cover $token." }
}

. (Join-Path $repo 'packaging/windows/installer/repair-target.ps1')
if ([Environment]::OSVersion.Platform -eq 'Win32NT' -and
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $fresh = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('Installer lock fixture ' + [guid]::NewGuid().ToString('N'))
    $nativeLock = $null
    try {
        $nativeLock = Open-InstallerLock $fresh $null $false
        Assert-RepairTree ([IO.DirectoryInfo]::new($fresh)) $nativeLock
        if ($nativeLock.Name -ne (Join-Path $fresh '.update-backup.lock')) { throw 'Fresh install acquired a different lock.' }
    } finally {
        if ($nativeLock) { $nativeLock.Dispose() }
        if ([IO.Directory]::Exists($fresh)) { [IO.Directory]::Delete($fresh,$true) }
    }
}
function New-DefaultInstallRoot($dir) { $dir.Create() }
function Assert-RepairRoot($dir) {}
function Assert-RepairObject($entry,$ancestor) {}
function Resolve-RepairApplication($path) { return [IO.DirectoryInfo]::new($path).FullName }
function Get-RepairScratch($app) { return [IO.Directory]::CreateDirectory((Join-Path $app '.repair-temp')).FullName }
function Warn($text) {}
function Ok($text) { $events.Add('ok') }
function Test-OwnService { return $owned }
function Start-OwnService { $events.Add('service') }
function Invoke-FixtureDoctor($path,$operation) {
    if ($path -ne (Join-Path $AppDir 'VPNRouter.CLI.exe') -or $operation -ne 'doctor') { throw 'Wrong doctor invocation.' }
    $events.Add('doctor')
    if ($case -eq 'missing-doctor') { throw 'Cannot start fixture CLI.' }
    $global:LASTEXITCODE = $doctorResult
}
function Start-Process($FilePath,$WorkingDirectory) {
    if ($FilePath -ne (Join-Path $AppDir 'VPNRouter.App.exe') -or $WorkingDirectory -ne $AppDir) { throw 'Wrong relaunch path.' }
    if ([IO.File]::Exists($marker)) { throw 'Marker still present at relaunch.' }
    if ($case -eq 'failed-launch') { throw 'Fixture child did not start.' }
    $events.Add('app')
}

$root = [IO.Path]::Combine([IO.Path]::GetTempPath(), [Guid]::NewGuid().ToString('N'))
$payloadDir = Join-Path $root 'payload'
$source = [IO.Directory]::CreateDirectory((Join-Path $payloadDir 'app'))
$nested = [IO.Directory]::CreateDirectory((Join-Path $source.FullName 'profiles'))
$events = [Collections.Generic.List[string]]::new()
try {
    [IO.File]::WriteAllText((Join-Path $payloadDir 'README.txt'), 'release document')
    [IO.File]::WriteAllText((Join-Path $source.FullName 'VPNRouter.App.exe'), 'verified fixture')
    [IO.File]::WriteAllText((Join-Path $nested.FullName 'payload.json'), '{}')
    foreach ($layout in 'flat','nested') {
        $installation = [IO.Directory]::CreateDirectory((Join-Path $root "Owner's [$layout] & router")).FullName
        $application = if ($layout -eq 'flat') { $installation } else { [IO.Directory]::CreateDirectory((Join-Path $installation 'app')).FullName }
        $marker = Join-Path $application '.update-failed'
        $unrelated = Join-Path $root '.update-failed'
        [IO.File]::WriteAllText($unrelated,'sibling')
        $target = Join-Path $application 'VPNRouter.App.exe'
        $lockPath = Join-Path $installation '.update-backup.lock'
        foreach ($mode in 'targeted','default-repair','reinstall') {
            $Repair = $mode -ne 'reinstall'
            $RepairApplicationDir = if ($mode -eq 'targeted') { $application }
            $cases = if ($Repair) {
                'busy','bad-copy','missing-doctor','abnormal','abnormal-quiet','new-marker','diagnostic','warning','success','quiet'
                if ($RepairApplicationDir) { 'failed-launch' }
            } else { 'busy','success' }
            foreach ($case in $cases) {
                $InstallRoot = if ($RepairApplicationDir) { Join-Path $root 'unused default' } else { $installation }
                $events.Clear()
                [IO.File]::WriteAllText($target,'current')
                [IO.File]::WriteAllText($marker,'original')
                if ($case -eq 'new-marker') { [IO.File]::Delete($marker) }
                $doctorResult = switch ($case) { diagnostic {2} warning {1} abnormal {-1073741819} abnormal-quiet {255} default {0} }
                $NoLaunch = $case -in 'abnormal-quiet','quiet'
                $svcWasRunning = $case -in 'quiet','warning'
                $owned = $case -ne 'warning'
                $held = $repairLock = $repairMarker = $null
                $failed = $false
                try {
                    if ($case -eq 'busy') {
                        # The competing caller uses the opposite entry point.
                        $other = if (!$RepairApplicationDir) { $application }
                        $held = Open-InstallerLock $installation $other ([bool]$other)
                    }
                    . $acquire
                    if ($AppDir -ne $application -or $repairLock.Name -ne $lockPath) { throw 'Wrong installation selected.' }
                    $events.Add('prepare')
                    if ($case -eq 'new-marker') { [IO.File]::WriteAllText($marker,'subsequent') }
                    if ($case -eq 'bad-copy') { Assert-TargetRepairCopy $source $AppDir }
                    & $wipe
                    if (![IO.File]::Exists($lockPath)) { throw 'Reinstall removed its open lock.' }
                    & $copy
                    & $complete
                } catch { $failed = $true; $detail = $_.Exception.Message }
                finally {
                    if ($repairMarker) { $repairMarker.Dispose() }
                    if ($repairLock) { $repairLock.Dispose() }
                    if ($held) { $held.Dispose() }
                }
                $success = $case -in 'success','diagnostic','warning','quiet'
                if ($failed -eq $success) { throw "Unexpected $layout/$mode/$case outcome: $detail" }
                if ($case -eq 'busy') {
                    if ($events.Count -or [IO.File]::ReadAllText($target) -ne 'current') { throw 'Busy installer changed live state.' }
                }
                if ($case -in 'abnormal','abnormal-quiet' -and $detail -notlike "*doctor exited with code $doctorResult*") { throw 'Wrong abnormal doctor result.' }
                if ($success) {
                    if ([IO.File]::Exists($marker) -or [IO.File]::ReadAllText($target) -ne 'verified fixture' -or
                        ![IO.File]::Exists((Join-Path $application 'profiles/payload.json'))) { throw "$layout/$mode/$case lost verified files or retained its marker (events: $events)." }
                    $expected = @('prepare')
                    if ($Repair) {
                        if ($owned -and $svcWasRunning) { $expected += 'service' }
                        $expected += 'doctor'
                        if ($RepairApplicationDir -and !$NoLaunch) { $expected += 'app' }
                        $expected += 'ok'
                    }
                    if (($events -join ',') -ne ($expected -join ',')) { throw "Unexpected $layout/$mode/$case events: $events" }
                } else {
                    $expectedMarker = if ($case -eq 'new-marker') { 'subsequent' } else { 'original' }
                    if ([IO.File]::ReadAllText($marker) -ne $expectedMarker -or $events.Contains('app') -or $events.Contains('ok')) { throw 'Failed repair consumed marker, launched or reported success.' }
                }
                if ([IO.File]::ReadAllText($unrelated) -ne 'sibling') { throw 'Another installation lost its marker.' }
            }
        }
    }
    # A root diagnostic left beside a nested installation belongs to the root, not to app/.
    $install = [IO.Directory]::CreateDirectory((Join-Path $root 'root-marker')).FullName
    $nestedApp = [IO.Directory]::CreateDirectory((Join-Path $install 'app')).FullName
    [IO.File]::WriteAllText((Join-Path $install '.update-failed'),'sibling')
    if ((Get-DefaultApplication $install) -ne $nestedApp -or (Open-TargetFailureMarker $nestedApp)) { throw 'Default repair selected a sibling marker.' }
    foreach ($Repair in $true,$false) {
        $InstallRoot = Join-Path $root "new-install-$Repair"
        $RepairApplicationDir = $null
        $repairLock = $repairMarker = $null
        try {
            . $acquire
            if ($repairMarker -or $AppDir -ne (Join-Path $InstallRoot 'app')) { throw 'Wrong first installation target.' }
            & $wipe
            & $copy
            if (![IO.File]::Exists($repairLock.Name) -or ![IO.File]::Exists((Join-Path $AppDir 'VPNRouter.App.exe'))) { throw 'Initial installation failed.' }
        } finally { if ($repairLock) { $repairLock.Dispose() } }
    }
} finally { [IO.Directory]::Delete($root,$true) }
'Default/targeted repair and reinstall: flat/nested contention, initial publication, lock preservation, marker ownership, service state, doctor 0/1/2 and abnormal exit with/without launch passed.'
