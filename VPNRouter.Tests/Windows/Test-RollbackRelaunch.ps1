# Execute the bundled rollback orchestration with harmless files and a recording process launcher.
$ErrorActionPreference = 'Stop'
$repo = [IO.Directory]::GetParent($PSScriptRoot).Parent.FullName
. (Join-Path $repo 'packaging/windows/installer/repair-environment.ps1')
$setEnvironment = (Get-Command Set-RepairEnvironment).ScriptBlock
$launcher = [IO.File]::ReadAllText((Join-Path $repo 'VPNRouter.App/Services/relaunch-rollback.ps1'))
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($launcher, [ref]$null, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$entry = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RollbackRelaunch'
}, $true)
. ([scriptblock]::Create($entry.Extent.Text))

$fixture = [IO.Path]::Combine([IO.Path]::GetTempPath(), [Guid]::NewGuid().ToString('N'))
$system = [IO.Path]::Combine($fixture, 'Windows', 'System32')
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { $system = [Environment]::SystemDirectory }
$root = [IO.Path]::Combine($fixture, "Owner's VPN [moved] & 100%")
$app = [IO.Path]::Combine($root, 'app')
$entryPoint = [scriptblock]::Create($ast.EndBlock.Statements[-1].Extent.Text)
function Invoke-FixtureRollback {
    $env:VPNROUTER_ROLLBACK_APP = $app
    & $entryPoint
}
$privateRoot = [IO.Path]::Combine($app, '.repair-temp')
$record = [Collections.Generic.List[object]]::new()
$oldEnvironment = [Environment]::GetEnvironmentVariables()
function Get-RepairScratch([string]$applicationDir) {
    $path = [IO.Path]::Combine($applicationDir, '.repair-temp')
    if (($refuse -eq 'restored' -and $path -eq $privateRoot) -or ($refuse -eq 'session' -and $path -ne $privateRoot)) {
        throw [UnauthorizedAccessException]::new('Unverified fixture ACL')
    }
    return [IO.Directory]::CreateDirectory($path).FullName
}
function Set-RepairEnvironment([string]$applicationDir) { & $setEnvironment $applicationDir $system }
function Start-RestoredApplication([Diagnostics.ProcessStartInfo]$info) {
    if ($info.UseShellExecute -or $info.WorkingDirectory -cne $app -or
        $info.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] -or $info.EnvironmentVariables['VPNROUTER_ROLLBACK_APP'] -or $env:PATH -like '*untrusted-fixture*' -or
        !$env:PATH.StartsWith($system) -or !$env:PSModulePath.StartsWith($system) -or
        $env:ComSpec -ne [IO.Path]::Combine($system, 'cmd.exe') -or
        !$env:TEMP.StartsWith($privateRoot + [IO.Path]::DirectorySeparatorChar) -or
        $env:TEMP -ne $env:TMP -or [IO.Directory]::GetFileSystemEntries($env:TEMP).Length -ne 0) {
        throw 'A rollback child inherited an unsafe path or environment.'
    }
    if ($refuse -eq 'creation') { throw 'Fixture child creation failure.' }
    $record.Add([pscustomobject]@{ Exe = $info.FileName; Scratch = $env:TEMP })
}
try {
    $backup = [IO.Path]::Combine($root, 'app.bak')
    $null = [IO.Directory]::CreateDirectory($backup)
    foreach ($name in 'VPNRouter.App.exe','VPNRouter.GUI.exe') { [IO.File]::WriteAllText([IO.Path]::Combine($backup, $name), 'fixture') }
    [IO.Directory]::Move($backup, $app)
    $gui = [IO.Path]::Combine($app, 'VPNRouter.GUI.exe')
    $fallback = [IO.Path]::Combine($app, 'VPNRouter.App.exe')
    $marker = [IO.Path]::Combine($app, '.update-failed')
    $env:TEMP = $env:TMP = $env:PATH = $env:DOTNET_STARTUP_HOOKS = 'untrusted-fixture'
    Invoke-FixtureRollback
    if ($record.Count -ne 1 -or $record[0].Exe -cne $gui) { throw 'Legacy GUI was not selected.' }
    [IO.File]::WriteAllText([IO.Path]::Combine($privateRoot, 'legacy-data'), 'older scratch')
    [IO.File]::Delete($gui)
    [IO.File]::WriteAllText($marker, 'old update')
    Invoke-FixtureRollback
    if ($record.Count -ne 2 -or $record[1].Exe -cne $fallback -or $record[0].Scratch -eq $record[1].Scratch -or [IO.File]::Exists($marker)) {
        throw 'App fallback, fresh scratch or marker handling failed.'
    }
    foreach ($refuse in 'restored','session','creation','missing-launcher') {
        [IO.File]::WriteAllText($marker, 'diagnostic')
        if ($refuse -eq 'missing-launcher') { [IO.File]::Delete($fallback) }
        $failed = $false
        try { Invoke-FixtureRollback } catch { $failed = $true }
        if (!$failed -or $record.Count -ne 2 -or ![IO.File]::Exists($marker)) { throw "Launched after refusal: $refuse" }
    }
} finally {
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) { [Environment]::SetEnvironmentVariable($key, [NullString]::Value, 'Process') }
    foreach ($key in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $oldEnvironment[$key], 'Process') }
    if ([IO.Directory]::Exists($fixture)) { [IO.Directory]::Delete($fixture, $true) }
}
'Rollback relaunch: legacy GUI, App fallback and four refusal scenarios passed.'
