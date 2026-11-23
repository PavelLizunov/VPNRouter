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
foreach ($name in @('Get-DriverImage','Test-LegacyImage','Test-NinaDriver','Test-ReferencesRoot','Assert-PrepareOwnership','Invoke-Prepare')) {
    $fn = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (!$fn) { throw "Missing installer decision $name" }
    . ([scriptblock]::Create($fn.Extent.Text))
}

$stopScript = [IO.Path]::Combine($repo, 'packaging/windows/installer/stop-vpnrouter.ps1')
$stopAst = [Management.Automation.Language.Parser]::ParseFile($stopScript, [ref]$null, [ref]$null)
foreach ($name in @('Get-InstallProcessImages','Test-ProcessImage','Test-ServiceImage')) {
    $fn = $stopAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($fn.Extent.Text))
}

# Use a literal Windows path join independently of the host operating system.
function Join-Path([string]$Path, [string]$ChildPath) { $Path.TrimEnd('\') + '\' + $ChildPath }
function Check([bool]$value, [string]$label) { if (!$value) { throw $label } }
# Exercise the same orchestration used by a silent upgrade with in-memory registry and SCM objects.
function Test-PrepareSequence {
    $services = [pscustomobject]@{ Keys = @{}; WriteOpens = 0; ConfigCalls = 0 }
    $services | Add-Member ScriptMethod OpenSubKey {
        param($name, [bool]$writable = $false)
        if ($writable) { $this.WriteOpens++ }
        return $this.Keys[$name]
    }
    function Add-Registration($name, $image, $type, $start) {
        $key = [pscustomobject]@{ Data = @{ ImagePath=$image; Type=$type; Start=$start } }
        $key | Add-Member ScriptMethod GetValue { param($name, $fallback, $options) return $this.Data[$name] }
        $key | Add-Member ScriptMethod SetValue { param($name, $value, $kind) $this.Data[$name] = $value }
        $key | Add-Member ScriptMethod Flush {}
        $key | Add-Member ScriptMethod Dispose {}
        $services.Keys[$name] = $key
    }
    function Get-CimInstance {
        param($ClassName, $Filter, $ErrorAction)
        if ($Filter -eq "Name='winws2.exe'") { return $fakeNinaProcess }
        return $fakeProcesses
    }
    function Add-Type { param($AssemblyName) }
    function New-Object {
        param($TypeName, $ArgumentList)
        if ($TypeName -ne 'System.ServiceProcess.ServiceController') { throw "Unexpected controller $TypeName" }
        $controller = [pscustomobject]@{ ServiceHandle = 1; Status = 'Running' }
        $controller | Add-Member ScriptMethod Dispose {}
        return $controller
    }
    $sc = {
        param($command, $name, $option, $value)
        if ($command -ne 'config' -or $option -ne 'start=' -or $value -ne 'disabled') { throw 'Unexpected SCM request' }
        $services.ConfigCalls++
        $services.Keys[$name].Data.Start = 4
        $global:LASTEXITCODE = 0
    }
    Add-Registration 'zapret' ('"' + $legacyBin + '\winws.exe" --old-choice') 16 2
    Add-Registration 'WinDivert' ('\??\' + $legacyBin + '\WinDivert64.sys') 1 2
    Add-Registration 'WinDivert15' 'C:\Other\WinDivert64.sys' 1 3
    $fakeProcesses = @([pscustomobject]@{ExecutablePath=($legacyBin + '\winws.exe')})
    $services.Keys['zapret'].Data['zapret-discord-youtube'] = 'general (ALT3)'
    $blocked = $false
    try { Invoke-Prepare } catch { $blocked = $_.Exception.Message -match 'blocked by service WinDivert15' }
    Check $blocked 'silent Prepare rejects conflict in the last driver'
    Check ($services.WriteOpens -eq 0 -and $services.ConfigCalls -eq 0) 'silent conflict keeps service and every driver untouched'
    Check ($services.Keys['zapret'].Data['zapret-discord-youtube'] -eq 'general (ALT3)') 'preserve old selection on conflict'
    Check (!$services.Keys['zapret'].Data.ContainsKey('VPNRouterPreUpgradeState')) 'no snapshot writes while blocked'
    $services.Keys.Remove('WinDivert15')
    $fakeProcesses = @([pscustomobject]@{ExecutablePath='C:\Other\bin\winws.exe'})
    $blocked = $false
    try { Invoke-Prepare } catch { $blocked = $_.Exception.Message -match 'winws process' }
    Check ($blocked -and $services.WriteOpens -eq 0 -and $services.ConfigCalls -eq 0) 'silent foreign process conflict prevents every write'
    $fakeProcesses = @()
    Invoke-Prepare
    Check ($services.ConfigCalls -eq 2 -and $services.WriteOpens -eq 2) 'silent Prepare disables two verified autostart registrations'
    foreach ($name in @('zapret','WinDivert')) {
        Check ($services.Keys[$name].Data.Start -eq 4 -and $services.Keys[$name].Data.VPNRouterPreUpgradeState -eq 18) 'recoverable active, automatic selection'
    }
    Invoke-Prepare
    Check ($services.ConfigCalls -eq 2 -and $services.WriteOpens -eq 2) 'repeat Prepare preserves the original snapshot'

    if ($Edition -ne 'VPNRouter' -or [IO.Path]::DirectorySeparatorChar -ne '\') { return }
    $scratch = [IO.Path]::Combine([IO.Path]::GetTempPath(), [Guid]::NewGuid().ToString('N'))
    try {
        $runtime = Join-Path $scratch 'runtime'
        $ninaBin = Join-Path $scratch 'nina'
        $protectedBin = Join-Path $runtime 'zapret\bin'
        $null = [IO.Directory]::CreateDirectory($ninaBin)
        $null = [IO.Directory]::CreateDirectory($protectedBin)
        $nina = Join-Path $ninaBin 'WinDivert64.sys'
        $installed = Join-Path $protectedBin 'WinDivert64.sys'
        [IO.File]::WriteAllBytes($nina, [byte[]]@(1, 2, 3))
        [IO.File]::WriteAllBytes($installed, [byte[]]@(1, 2, 4))
        Add-Registration 'zapret' ('"' + $legacyBin + '\winws.exe" --old-choice') 16 2
        Add-Registration 'WinDivert' ('\??\' + $nina) 1 3

        $blocked = $false
        try { Invoke-Prepare } catch { $blocked = $_.Exception.Message -match 'blocked by service WinDivert' }
        Check ($blocked -and $services.ConfigCalls -eq 2 -and $services.WriteOpens -eq 2) 'mismatched NINA driver fails before any change'
        [IO.File]::WriteAllBytes($installed, [byte[]]@(1, 2, 3))
        $fakeNinaProcess = [pscustomobject]@{ ExecutablePath=(Join-Path $ninaBin 'winws2.exe') }
        $blocked = $false
        try { Invoke-Prepare } catch { $blocked = $_.Exception.Message -match 'running winws2 process' }
        Check ($blocked -and $services.ConfigCalls -eq 2 -and $services.WriteOpens -eq 2) 'running NINA capture blocks changes'
        $fakeNinaProcess = $null

        Invoke-Prepare
        Check ($services.ConfigCalls -eq 3 -and $services.WriteOpens -eq 3) 'compatible NINA driver leaves only Zapret to migrate'
        Check ($services.Keys['zapret'].Data.VPNRouterPreUpgradeState -eq 18) 'Zapret retained its original startup state'
        Check ($services.Keys['WinDivert'].Data.Start -eq 3 -and
            !$services.Keys['WinDivert'].Data.ContainsKey('VPNRouterPreUpgradeState')) 'the NINA registration was preserved'
    } finally {
        if ([IO.Directory]::Exists($scratch)) { [IO.Directory]::Delete($scratch, $true) }
    }
}

foreach ($edition in @('VPNRouter','VPNRouterBusiness')) {
    $legacyBin = "D:\Program Data\$edition\zapret\bin"
    $package = "E:\Program Files\$edition Zapret Runtime"
    $other = if ($edition -eq 'VPNRouter') { 'VPNRouterBusiness' } else { 'VPNRouter' }
    $app = "E:\Program Files\$edition\app"
    $main = "$app\VPNRouter.Service.exe"
    $images = Get-InstallProcessImages $app "D:\Program Data\$edition"
    Check (Test-ProcessImage "$app\VPNRouter.App.exe") 'this installation app'
    Check (!(Test-ProcessImage "$legacyBin\winws.exe")) 'legacy winws is managed by the migration'
    Check (!(Test-ProcessImage "$package\zapret\bin\winws.exe")) 'protected winws is managed by its owner'
    Check (!(Test-ProcessImage "D:\Program Data\$other\zapret\bin\winws.exe")) 'other edition process'
    Check (!(Test-ProcessImage "$app\unknown.exe")) 'unknown process beside installed app'
    Check (!(Test-ProcessImage '')) 'unreadable process'
    Check (Test-ServiceImage ('"' + $main + '" --service') $main) 'installed main service'
    Check (!(Test-ServiceImage ('"' + $main + '.old" --service') $main)) 'similar service executable'
    Check (!(Test-ServiceImage ('"E:\Program Files\' + $other + '\app\VPNRouter.Service.exe" --service') $main)) 'other edition main service'
    $runtime = $package
    Test-PrepareSequence
    $verified = [pscustomobject]@{ Name='zapret'; Type=16; Image=('"' + $legacyBin + '\winws.exe" --wf-tcp=443') }
    $ownDriver = [pscustomobject]@{ Name='WinDivert14'; Type=1; Image=('\??\' + $legacyBin + '\WinDivert64.sys') }
    Assert-PrepareOwnership @($verified, $ownDriver) @([pscustomobject]@{ ExecutablePath=($legacyBin + '\winws.exe') })
    $foreign = [pscustomobject]@{ Name='WinDivert15'; Type=1; Image='C:\Other\WinDivert64.sys' }
    $blocked = $false
    try { Assert-PrepareOwnership @($verified, $ownDriver, $foreign) @() } catch { $blocked = $true }
    Check $blocked 'inspect every driver before any write'
    $blocked = $false
    try { Assert-PrepareOwnership @($verified) @([pscustomobject]@{ ExecutablePath=$null }) } catch { $blocked = $true }
    Check $blocked 'unverifiable process blocks prepare'
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
