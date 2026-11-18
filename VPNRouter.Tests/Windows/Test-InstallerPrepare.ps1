# Exercise the standalone preparation boundary with a synthetic package and no service registrations.
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$installer = [IO.File]::ReadAllText((Join-Path $root 'packaging/windows/install.ps1'))
$first = $installer.IndexOf('$prepare = Join-Path $payloadDir')
$last = $installer.IndexOf('$svc = Get-Service -Name VPNRouter', $first)
if ($first -lt 0 -or $last -le $first) { throw 'Missing standalone preparation before service shutdown.' }
$prepareBody = $installer.Substring($first, $last - $first)
$stagingDir = Join-Path ([IO.Path]::GetTempPath()) ('installer-prepare-test-' + [guid]::NewGuid().ToString('N'))
$payloadDir = Join-Path $stagingDir 'payload'
$app = Join-Path $payloadDir 'app'
$WindowsPowerShell = (Get-Process -Id $PID).Path
try {
    [IO.Directory]::CreateDirectory($app) | Out-Null
    $marker = Join-Path $stagingDir 'called.txt'
    $stub = 'param($Edition,$Operation)' + [Environment]::NewLine +
        "[IO.File]::WriteAllText('" + $marker.Replace("'","''") + "', (`$Edition + ':' + `$Operation))"
    $packaged = Join-Path $app 'zapret-maintenance.ps1'
    [IO.File]::WriteAllText($packaged, $stub)
    & ([scriptblock]::Create($prepareBody))
    if ([IO.File]::ReadAllText($marker) -ne 'VPNRouter:Prepare') { throw 'Packaged helper was not prepared.' }

    [IO.File]::Delete($packaged)
    [IO.File]::Delete($marker)
    $historical = $prepareBody.Replace('__VPNROUTER_MAINTENANCE_BASE64__',
        [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($stub)))
    & ([scriptblock]::Create($historical))
    if ([IO.File]::ReadAllText($marker) -ne 'VPNRouter:Prepare') { throw 'Historical package preparation failed.' }

    [IO.File]::WriteAllText($packaged, 'param($Edition,$Operation); exit 9')
    $failed = $false
    try { & ([scriptblock]::Create($prepareBody)) } catch { $failed = $true }
    if (!$failed) { throw 'Preparation failure allowed publication to continue.' }
} finally {
    if ([IO.Directory]::Exists($stagingDir)) { [IO.Directory]::Delete($stagingDir, $true) }
}
Write-Output 'Standalone packaged helper, historical helper and failure gate passed.'
