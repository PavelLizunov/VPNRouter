$ErrorActionPreference = 'Stop'
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'packaging/windows/installer/stop-vpnrouter.ps1'), [ref]$null, [ref]$null)
foreach ($name in 'Test-ServiceImage','Get-OwnMainService') {
    $fn = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($fn.Extent.Text))
}
$serviceExe = 'D:\Relocated Business\app\VPNRouter.Service.exe'
$expected = '"' + $serviceExe + '" --service'
$foreign = '"D:\Other Edition\app\VPNRouter.Service.exe" --service'
$opened = [Collections.Generic.List[string]]::new()
function Get-CimInstance($ClassName,$Filter) {
    $key = $Filter.Split("'")[1]
    if ($images.ContainsKey($key)) { return [pscustomobject]@{PathName=$images[$key]} }
}
function Get-Service($Name) {
    $opened.Add($Name)
    $controller = [pscustomobject]@{ ServiceName=$Name; ServiceHandle=1; ServiceType=16 }
    $controller | Add-Member ScriptMethod Dispose {}
    return $controller
}
$Business = $true
foreach ($name in 'VPNRouterBusiness','VPNRouter') {
    $other = if ($name -eq 'VPNRouter') { 'VPNRouterBusiness' } else { 'VPNRouter' }
    $images = @{ $name=$expected; $other=$foreign }
    $MainServiceName = $null
    $opened.Clear()
    if ((Get-OwnMainService).ServiceName -ne $name -or ($opened -join ',') -ne $name) { throw 'Installer selected a foreign registration.' }
    $MainServiceName = $name
    if ((Get-OwnMainService).ServiceName -ne $name) { throw 'Failed to find the stopped registration by its recorded name.' }
}
$images = @{ VPNRouter=$expected; VPNRouterBusiness=$expected }
$MainServiceName = $null
$blocked = $false
try { Get-OwnMainService | Out-Null } catch { $blocked = $true }
if (!$blocked) { throw 'Ambiguous registration selection.' }
$Business = $false
if ((Get-OwnMainService).ServiceName -ne 'VPNRouter') { throw 'Wrong Standard registration.' }
'Historical, edition-specific, conflicting and Standard installer registrations passed.'
