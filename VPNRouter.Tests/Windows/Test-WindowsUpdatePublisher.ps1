# Exercise the installed publisher's orchestration with ordinary files and fake process boundaries.
$ErrorActionPreference = 'Stop'
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'packaging/windows/installer/publish-windows-update.ps1'), [ref]$null, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join [Environment]::NewLine) }
$entry = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-WindowsPublication' }, $true)
if (!$entry) { throw 'Cannot find the publisher.' }
. ([scriptblock]::Create($entry.Extent.Text))

$InstallDir = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
$destination = Join-Path $InstallDir 'app'
$Source = Join-Path $InstallDir 'release'
$DataDir = $InstallDir
$log = Join-Path $InstallDir 'update.log'
$systemShell = 'Invoke-FixtureShell'
$copy = 'Copy-Fixture'
$stop = 'stop-vpnrouter.ps1'
$maintenance = 'zapret-maintenance.ps1'
$Fixture = $false
$SkipRelaunch = $true
$state = @{ LiveChanges = $false }
$events = [Collections.Generic.List[string]]::new()
function Invoke-FixtureShell {
    if ($args -contains 'Prepare') {
        $events.Add('prepare')
        $global:LASTEXITCODE = $prepareResult
    } elseif ($args -contains '-RestartService') {
        $events.Add('restart')
        $global:LASTEXITCODE = 0
    } else {
        $events.Add('stop')
        $global:LASTEXITCODE = 10
    }
}
function Copy-Fixture {
    $events.Add('copy')
    [IO.File]::Copy((Join-Path $Source 'payload.dll'), (Join-Path $destination 'payload.dll'), $true)
    $global:LASTEXITCODE = 0
    '1 File(s) copied'
}
function Start-Process {
    param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$Wait, [switch]$PassThru)
    if ($ArgumentList -ne '--migrate-zapret' -or $FilePath -ne (Join-Path $destination 'VPNRouter.App.exe')) {
        throw 'Unexpected publisher child.'
    }
    $events.Add('restore')
    $process = [pscustomobject]@{ ExitCode = 2 }
    $process | Add-Member ScriptMethod Dispose {}
    $process
}
try {
    $null = [IO.Directory]::CreateDirectory($Source)
    $null = [IO.Directory]::CreateDirectory($destination)
    [IO.File]::WriteAllText((Join-Path $Source 'payload.dll'), 'release')
    [IO.File]::WriteAllText((Join-Path $Source 'zapret-maintenance.ps1'), '')
    [IO.File]::WriteAllText((Join-Path $destination 'payload.dll'), 'previous')
    $prepareResult = 1
    $failed = $false
    try { Invoke-WindowsPublication } catch { $failed = $true }
    if (!$failed -or ($events -join ',') -ne 'prepare' -or $state.LiveChanges -or
        [IO.File]::ReadAllText((Join-Path $destination 'payload.dll')) -ne 'previous') { throw 'Prepare must precede publication.' }
    $events.Clear()
    $prepareResult = 0
    Invoke-WindowsPublication
    if (($events -join ',') -ne 'prepare,stop,copy,restore,restart' -or
        [IO.File]::ReadAllText((Join-Path $destination 'payload.dll')) -ne 'release') { throw 'Upgrade order or content is incorrect.' }
    $events.Clear()
    [IO.File]::Delete((Join-Path $Source 'zapret-maintenance.ps1'))
    Invoke-WindowsPublication
    if (($events -join ',') -ne 'prepare,stop,copy,restart') { throw 'Older release was passed an unsupported migration switch.' }
    $events.Clear()
    $Fixture = $true
    Invoke-WindowsPublication
    if (($events -join ',') -ne 'copy') { throw 'CI fixture accessed a system operation.' }
    'Windows publisher: 4 offline cases passed.'
} finally {
    [IO.Directory]::Delete($InstallDir, $true)
}
