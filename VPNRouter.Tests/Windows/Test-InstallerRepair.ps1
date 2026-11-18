# Exercise the actual targeted copy and post-repair branches against an ordinary directory tree.
$ErrorActionPreference = 'Stop'
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$installer = Join-Path $repo 'packaging/windows/install.ps1'
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
function Get-Branch([string]$condition, [string]$operation) {
    $nodes = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -eq $condition -and $node.Clauses[0].Item2.Extent.Text.Contains($operation)
    }, $true))
    if ($nodes.Count -ne 1) { throw "Could not find the installer branch for $condition." }
    return $nodes[0].Clauses[0].Item2.Extent.Text
}
$copy = [scriptblock]::Create((Get-Branch '$RepairApplicationDir' 'Copy-Item'))
$complete = [scriptblock]::Create((Get-Branch '$Repair' ' doctor').Replace("& (Join-Path ", "Invoke-FixtureDoctor (Join-Path "))
foreach ($name in 'Complete-TargetRepair','Assert-TargetRepairCopy','Open-TargetFailureMarker') {
    $entry = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($entry.Extent.Text))
}
function Warn($text) {}
. (Join-Path $repo 'packaging/windows/installer/repair-target.ps1')
$lockStatement = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$repairLock' -and $node.Right.Extent.Text.Contains('Open-RepairInstallLock') }, $true)
$acquire = [scriptblock]::Create($lockStatement.Extent.Text)
$transaction = $lockStatement.Parent
while ($transaction -and $transaction -isnot [Management.Automation.Language.TryStatementAst]) { $transaction = $transaction.Parent }
if (!$transaction.Finally.Extent.Text.Contains('$repairLock.Dispose()') -or
    $transaction.Body.Extent.Text.IndexOf($lockStatement.Extent.Text) -gt $transaction.Body.Extent.Text.IndexOf('-Operation Prepare') -or
    !$transaction.Body.Extent.Text.Contains('Assert-TargetRepairCopy') -or !$transaction.Body.Extent.Text.Contains('Complete-TargetRepair $AppDir')) {
    throw 'Targeted repair is not locked through Prepare, copying, verification and relaunch.'
}

function Assert-RepairRoot($dir) {}
function Assert-RepairObject($entry,$ancestor) {}
function Get-RepairScratch($app) { return [IO.Directory]::CreateDirectory((Join-Path $app '.repair-temp')).FullName }
$doctorResult = 0
$root = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
$payloadDir = Join-Path $root 'payload'
$source = [IO.Directory]::CreateDirectory((Join-Path $payloadDir 'app'))
$nested = [IO.Directory]::CreateDirectory((Join-Path $source.FullName 'profiles'))
$AppDir = [IO.Directory]::CreateDirectory((Join-Path $root "Owner's [relocated] & router")).FullName
$InstallRoot = [IO.Directory]::CreateDirectory((Join-Path $root 'unrelated')).FullName
$RepairApplicationDir = $AppDir
$events = [Collections.Generic.List[string]]::new()
function Test-OwnService { return $owned }
function Start-OwnService { $events.Add('service') }
function Invoke-FixtureDoctor($path,$operation) {
    if ($path -ne (Join-Path $AppDir 'VPNRouter.CLI.exe') -or $operation -ne 'doctor') { throw 'Wrong doctor invocation.' }
    $events.Add('doctor')
    if ($case -eq 'bad-doctor') { throw 'Fixture CLI could not start.' }
    $global:LASTEXITCODE = $doctorResult
}
function Start-Process($FilePath,$WorkingDirectory) {
    if ($FilePath -ne (Join-Path $AppDir 'VPNRouter.App.exe') -or $WorkingDirectory -ne $AppDir) { throw 'Wrong repair relaunch path.' }
    if ([IO.File]::Exists((Join-Path $AppDir '.update-failed'))) { throw 'Repair launched before retiring its marker.' }
    if ($launchFails) { throw 'Fixture launch failure' }
    $events.Add('app')
}
try {
    [IO.File]::WriteAllText((Join-Path $source.FullName 'VPNRouter.App.exe'), 'verified fixture')
    [IO.File]::WriteAllText((Join-Path $nested.FullName 'payload.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $InstallRoot 'marker'), 'unchanged')
    & (& $copy)
    if ([IO.File]::ReadAllText((Join-Path $AppDir 'VPNRouter.App.exe')) -ne 'verified fixture' -or
        ![IO.File]::Exists((Join-Path $AppDir 'profiles/payload.json')) -or
        @([IO.Directory]::EnumerateFileSystemEntries($InstallRoot)).Count -ne 1) { throw 'Repair copied outside its target or lost payload files.' }
    foreach ($case in @(
        @{ running = $true; owner = $true; quiet = $false; expected = 'service,doctor,app' },
        @{ running = $false; owner = $true; quiet = $false; expected = 'doctor,app' },
        @{ running = $true; owner = $false; quiet = $true; expected = 'doctor' },
        @{ running = $true; owner = $true; quiet = $true; expected = 'service,doctor' }
    )) {
        $events.Clear()
        $svcWasRunning = $case.running
        $owned = $case.owner
        $NoLaunch = $case.quiet
        & (& $complete)
        if (($events -join ',') -ne $case.expected) { throw "Unexpected repair events: $events" }
    }
    foreach ($layout in 'flat','nested') {
        $install = [IO.Directory]::CreateDirectory((Join-Path $root $layout)).FullName
        $AppDir = if ($layout -eq 'flat') { $install } else { [IO.Directory]::CreateDirectory((Join-Path $install 'app')).FullName }
        $RepairApplicationDir = $AppDir
        $marker = Join-Path $AppDir '.update-failed'
        $unrelated = Join-Path $root '.update-failed'
        [IO.File]::WriteAllText($unrelated, 'sibling')
        $target = Join-Path $AppDir 'VPNRouter.App.exe'
        $lockPath = Join-Path $install '.update-backup.lock'
        $owned = $true
        $svcWasRunning = $false
        $NoLaunch = $false
        foreach ($case in 'busy','bad-copy','bad-doctor','failed-launch','new-marker','doctor-diagnostic','success') {
            $events.Clear()
            [IO.File]::WriteAllText($target, 'current')
            [IO.File]::WriteAllText($marker, 'prior diagnostic')
            $doctorResult = if ($case -eq 'doctor-diagnostic') { 2 } else { 0 }
            $launchFails = $case -eq 'failed-launch'
            $held = if ($case -eq 'busy') { [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
            $repairLock = $repairMarker = $null
            if ($case -eq 'new-marker') { [IO.File]::Delete($marker) }
            $failed = $false
            try {
                . $acquire
                $repairMarker = Open-TargetFailureMarker $AppDir
                $events.Add('prepare')
                if ($case -eq 'new-marker') { [IO.File]::WriteAllText($marker, 'subsequent failure') }
                if ($case -eq 'bad-copy') {
                    # Check the real post-copy verifier against the incomplete target.
                    Assert-TargetRepairCopy ([IO.DirectoryInfo]::new((Join-Path $payloadDir 'app'))) $AppDir
                }
                & (& $copy)
                & (& $complete)
            } catch { $failed = $true }
            finally { if ($repairMarker) { $repairMarker.Dispose() }; if ($repairLock) { $repairLock.Dispose() }; if ($held) { $held.Dispose() } }
            if ($failed -ne ($case -notin @('success','doctor-diagnostic'))) { throw "Unexpected repair outcome: $layout/$case" }
            if ($case -eq 'busy' -and ($events.Count -ne 0 -or [IO.File]::ReadAllText($target) -ne 'current')) { throw 'Contended repair mutated the installation.' }
            if ($case -in @('success','doctor-diagnostic')) {
                if ([IO.File]::Exists($marker) -or ($events -join ',') -ne 'prepare,doctor,app') { throw 'Successful repair left marker or missed launch.' }
            } elseif ($case -eq 'new-marker') {
                if ([IO.File]::ReadAllText($marker) -ne 'subsequent failure') { throw 'Retired a newer marker.' }
            } elseif ([IO.File]::ReadAllText($marker) -ne 'prior diagnostic') { throw 'Repair failure lost original marker.' }
            if ([IO.File]::ReadAllText($unrelated) -ne 'sibling') { throw 'Repair consumed another marker.' }
        }
    }
} finally { [IO.Directory]::Delete($root, $true) }
'Targeted repair: service state and flat/nested contention, copy and startup failures, doctor diagnostics and successful marker retirement passed.'
