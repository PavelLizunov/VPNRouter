# Exercise the standalone preparation boundary with a synthetic package and no service registrations.
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$installer = [IO.File]::ReadAllText((Join-Path $root 'packaging/windows/install.ps1'))
$ast = [Management.Automation.Language.Parser]::ParseInput($installer, [ref]$null, [ref]$null)
$loader = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InstallerHelper' }, $true)
. ([scriptblock]::Create($loader.Extent.Text))
# Verify checkout pins as data. Only the synthetic policy below is ever executed.
$installerHelpers = Join-Path $root 'packaging/windows/installer'
$references = [regex]::Matches($installer, "Get-InstallerHelper '(__[A-Z_0-9]+__)' '([a-z-]+\.ps1)' '([a-f0-9]{64})'")
if ($references.Count -ne 3) { throw 'Missing local installer source digests.' }
foreach ($match in $references) {
    $reference = Get-InstallerHelper $match.Groups[1].Value $match.Groups[2].Value $match.Groups[3].Value
    if (!$reference.Length) { throw 'Missing installer helper source.' }
}
$installerHelpers = $null
$first = $installer.IndexOf('$canRestoreZapret = [IO.File]::Exists(')
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
    $embedded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($stub))
    $published = [scriptblock]::Create($prepareBody.Replace('__VPNROUTER_MAINTENANCE_BASE64__', $embedded))
    $packaged = Join-Path $app 'zapret-maintenance.ps1'
    [IO.File]::WriteAllText($packaged, 'param($Edition,$Operation); exit 9')
    . $published
    if (!$canRestoreZapret -or [IO.File]::ReadAllText($marker) -ne 'VPNRouter:Prepare') { throw 'Wrong Prepare policy for package with helper.' }

    [IO.File]::Delete($packaged)
    [IO.File]::Delete($marker)
    . $published
    if ($canRestoreZapret -or [IO.File]::ReadAllText($marker) -ne 'VPNRouter:Prepare') { throw 'Wrong historical package preparation.' }

    # A historical target accepts the change, but the current policy blocks it.
    [IO.File]::WriteAllText($packaged, 'param($Edition,$Operation); exit 0')
    $denied = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('param($Edition,$Operation); exit 9'))
    foreach ($policy in @($denied, '__VPNROUTER_MAINTENANCE_BASE64__', '')) {
        $failed = $false
        try { & ([scriptblock]::Create($prepareBody.Replace('__VPNROUTER_MAINTENANCE_BASE64__', $policy))) } catch { $failed = $true }
        if (!$failed) { throw 'Unverified or failed installer policy allowed publication to continue.' }
    }
} finally {
    if ([IO.Directory]::Exists($stagingDir)) { [IO.Directory]::Delete($stagingDir, $true) }
}
Write-Output 'Current Prepare policy, both target layouts and all failure gates passed.'
