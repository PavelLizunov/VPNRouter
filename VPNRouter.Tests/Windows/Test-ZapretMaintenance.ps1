# Exercise just the installer decisions with synthetic Windows paths, on any PowerShell host.
# Do not invoke the installer entry point or open the registry.
$ErrorActionPreference = 'Stop'
$repo = (Get-Item -LiteralPath $PSScriptRoot).Parent.Parent.FullName
foreach ($path in @('packaging/windows/install.ps1', 'packaging/windows/uninstall.ps1',
        'packaging/windows/installer/stop-vpnrouter.ps1', 'packaging/windows/installer/zapret-maintenance.ps1')) {
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::Combine($repo, $path), [ref]$null, [ref]$errors)
    if ($errors.Count) { throw ($errors.Message -join [Environment]::NewLine) }
}
$known = @('zapret', 'WinDivert', 'WinDivert14', 'WinDivert15')
foreach ($name in @('Test-LegacyImage','Test-ReferencesRoot')) {
    $fn = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (!$fn) { throw "Missing installer decision $name" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$stopScript = [IO.Path]::Combine($repo, 'packaging/windows/installer/stop-vpnrouter.ps1')
$stopAst = [Management.Automation.Language.Parser]::ParseFile($stopScript, [ref]$null, [ref]$null)
foreach ($name in @('Test-ProcessImage','Test-ServiceImage')) {
    $fn = $stopAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($fn.Extent.Text))
}

# Use a literal Windows path join independently of the host operating system.
function Join-Path([string]$Path, [string]$ChildPath) { $Path.TrimEnd('\') + '\' + $ChildPath }
function Check([bool]$value, [string]$label) { if (!$value) { throw $label } }
foreach ($edition in @('VPNRouter','VPNRouterBusiness')) {
    $legacyBin = "D:\Program Data\$edition\zapret\bin"
    $package = "E:\Program Files\$edition Zapret Runtime"
    $other = if ($edition -eq 'VPNRouter') { 'VPNRouterBusiness' } else { 'VPNRouter' }
    $app = "E:\Program Files\$edition\app"
    $main = "$app\VPNRouter.Service.exe"
    $images = @("$app\VPNRouter.App.exe", "$legacyBin\winws.exe")
    Check (Test-ProcessImage "$legacyBin\winws.exe") 'this edition process'
    Check (!(Test-ProcessImage "D:\Program Data\$other\zapret\bin\winws.exe")) 'other edition process'
    Check (!(Test-ProcessImage "$app\unknown.exe")) 'unknown process beside installed app'
    Check (!(Test-ProcessImage '')) 'unreadable process'
    Check (Test-ServiceImage ('"' + $main + '" --service') $main) 'installed main service'
    Check (!(Test-ServiceImage ('"' + $main + '.old" --service') $main)) 'similar service executable'
    Check (!(Test-ServiceImage ('"E:\Program Files\' + $other + '\app\VPNRouter.Service.exe" --service') $main)) 'other edition main service'
    Check (Test-LegacyImage 'zapret' ('"' + $legacyBin + '\winws.exe" --wf-tcp=443') 16) 'verified legacy service'
    Check (!(Test-LegacyImage 'zapret' ($legacyBin + '\winws.exe --wf-tcp=443') 16)) 'unquoted space in executable'
    Check (!(Test-LegacyImage 'zapret' ('"D:\Program Data\' + $other + '\zapret\bin\winws.exe"') 16)) 'other edition'
    Check (!(Test-LegacyImage 'zapret' ('"' + $legacyBin + '\winws.exe"') 1)) 'wrong service type'
    Check (Test-LegacyImage 'WinDivert' ('\??\' + $legacyBin + '\WinDivert64.sys') 1) 'verified driver image'
    Check (Test-LegacyImage 'WinDivert' ($legacyBin + '\WinDivert64.sys') 1) 'native absolute driver path with spaces'
    Check (Test-LegacyImage 'WinDivert14' ('"\??\' + $legacyBin + '\WinDivert32.sys"') 1) 'quoted NT driver path'
    Check (Test-LegacyImage 'WinDivert15' ('"\\?\' + $legacyBin + '\WinDivert64.sys"') 1) 'quoted extended driver path'
    Check (!(Test-LegacyImage 'WinDivert' ('"\??\' + $legacyBin + '\WinDivert64.sys') 1)) 'unbalanced driver path'
    Check (!(Test-LegacyImage 'WinDivert' ('\??\D:\Program Data\' + $other + '\zapret\bin\WinDivert64.sys') 1)) 'foreign native driver'
    Check (!(Test-LegacyImage 'unrelated' ($legacyBin + '\WinDivert64.sys') 1)) 'unknown driver name'
    Check (!(Test-LegacyImage 'unrelated' ('"' + $legacyBin + '\winws.exe"') 16)) 'other service'
    Check (Test-ReferencesRoot 'WinDivert15' ('\??\' + $package + '\zapret\bin\WinDivert64.sys') $package) 'retain registered driver'
    Check (Test-ReferencesRoot 'unrelated' ('"' + $package + '\zapret\bin\winws.exe"') $package) 'retain other registered consumer'
    Check (Test-ReferencesRoot 'WinDivert' $null $package) 'retain unverifiable WinDivert'
    Check (Test-ReferencesRoot 'zapret' 'D:\PROGRA~1\VPNROU~1\zapret\bin\winws.exe' $package) 'retain ambiguous alias'
    Check (!(Test-ReferencesRoot 'other' 'D:\Tools\other.exe' $package)) 'independent registration'
}
Write-Output 'Installer scripts parsed. Both edition ownership and purge decision suites passed.'
