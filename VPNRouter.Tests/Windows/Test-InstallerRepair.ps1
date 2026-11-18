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
}
function Start-Process($FilePath,$WorkingDirectory) {
    if ($FilePath -ne (Join-Path $AppDir 'VPNRouter.App.exe') -or $WorkingDirectory -ne $AppDir) { throw 'Wrong repair relaunch path.' }
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
} finally { [IO.Directory]::Delete($root, $true) }
'Targeted repair copy, service ownership/state and relaunch cases passed.'
