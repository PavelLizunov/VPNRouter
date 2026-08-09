# Runs only through tools/brat-verify.ps1 on the fixed WINBRAT test VM.
# It never reads, hashes, copies or returns opaque fixture contents.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'Install', 'Cycle', 'Cleanup')]
    [string]$Operation,

    [ValidateSet('Control', 'Target')]
    [string]$Profile = 'Target'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::MachineName -ine 'WINBRAT') {
    throw 'Official-client helper may run only on WINBRAT.'
}

$ClientExe = 'C:\Program Files\AmneziaWG\amneziawg.exe'
$ExpectedClientSha256 = 'dcd5ace18c26a58dd632b337f769673be14a288cfc04ba37f69587884d3806be'
$ExpectedSignerThumbprint = '141D90A1BA8F61863FBEDDF7DD1D66C1D1E0B128'
$ExpectedMsiSha256 = '1b7308d0c74685193dee5d30fd30f370b5a2748a7f648869cd16f25286efc784'
$ExpectedPayloadSha256 = '5855167c4c89efa5c5adbd0933ee4269382785bb35d6b04f7a5fd27d80f72934'
$WorkRoot = 'C:\r4review\official-ab\current'
$MsiPath = Join-Path $WorkRoot 'amneziawg.msi'
$PayloadZip = Join-Path $WorkRoot 'payload.zip'
$PayloadRoot = Join-Path $WorkRoot 'payload'
$PayloadExe = Join-Path $PayloadRoot 'VPNRouter.Tools.WinbratLoadGen.exe'
$WatchdogTask = 'VPNRouterOfficialAwgWatchdog'
$WatchdogScript = Join-Path $WorkRoot 'watchdog.ps1'
$WatchdogState = Join-Path $WorkRoot 'watchdog-state.txt'
$EndpointHost = 'loadtest.vpn.ninitux.com'
$FixtureRoot = 'C:\ProgramData\VPNRouterTestFixtures\AWG'
$ProfileMap = @{
    Control = [ordered]@{
        Fixture = Join-Path $FixtureRoot 'VPNRouter-AB-AWG-Control.conf.dpapi'
        Attestation = Join-Path $FixtureRoot 'VPNRouter-AB-AWG-Control.tailscale-safe'
        Service = 'AmneziaWGTunnel$VPNRouter-AB-AWG-Control'
        Adapter = 'VPNRouter-AB-AWG-Control'
    }
    Target = [ordered]@{
        Fixture = Join-Path $FixtureRoot 'VPNRouter-AB-AWG.conf.dpapi'
        Attestation = Join-Path $FixtureRoot 'VPNRouter-AB-AWG.tailscale-safe'
        Service = 'AmneziaWGTunnel$VPNRouter-AB-AWG'
        Adapter = 'VPNRouter-AB-AWG'
    }
}
$Selected = $ProfileMap[$Profile]
$Semaphore = New-Object System.Threading.Semaphore(1, 1, 'Global\VPNRouterOfficialAwgAB')
$SemaphoreHeld = $false
$script:WatchdogFired = $false
$script:LastCleanupStateClean = $false

function New-Result {
    param(
        [Parameter(Mandatory = $true)] [ValidateSet('PASS', 'FAIL', 'BLOCKED', 'ABORTED')] [string]$Status,
        [Parameter(Mandatory = $true)] [string]$Lifecycle,
        [string]$StartedUtc = ([DateTimeOffset]::UtcNow.ToString('o')),
        [bool]$ManagementRouteIntact = $false,
        [bool]$ExpectedAdapterRoute = $false,
        [bool]$AdapterByteCorrelation = $false,
        [bool]$CleanTeardown = $false,
        [hashtable]$Metrics = @{}
    )
    [ordered]@{
        Status = $Status
        Client = 'AmneziaWG'
        Profile = $Profile
        Operation = $Operation
        Lifecycle = $Lifecycle
        StartedUtc = $StartedUtc
        EndedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        ManagementRouteIntact = $ManagementRouteIntact
        ExpectedAdapterRoute = $ExpectedAdapterRoute
        AdapterByteCorrelation = $AdapterByteCorrelation
        CleanTeardown = $CleanTeardown
        Metrics = $Metrics
    }
}

function Get-OfficialClientBinaryState {
    $exists = Test-Path -LiteralPath $ClientExe -PathType Leaf
    if (-not $exists) { return [ordered]@{ Exists = $false; Valid = $false } }
    try {
        $hashValid = (Get-FileHash -LiteralPath $ClientExe -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedClientSha256
        $signature = Get-AuthenticodeSignature -LiteralPath $ClientExe
        $signatureValid = $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
            $null -ne $signature.SignerCertificate -and
            $signature.SignerCertificate.Thumbprint -eq $ExpectedSignerThumbprint
        return [ordered]@{ Exists = $true; Valid = [bool]($hashValid -and $signatureValid) }
    }
    catch { return [ordered]@{ Exists = $true; Valid = $false } }
}

function Test-OfficialClientBinary {
    return [bool](Get-OfficialClientBinaryState).Valid
}

function Test-ProtectedAcl {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Container', 'Fixture', 'Marker')]
        [string]$Kind
    )

    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
        if (-not $acl.AreAccessRulesProtected) { return $false }

        $systemSid = 'S-1-5-18'
        $administratorsSid = 'S-1-5-32-544'
        $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $secondarySid = if ($Kind -eq 'Fixture') { $administratorsSid } else { $currentSid }
        $allowed = @($systemSid, $secondarySid)
        $granted = @{ $systemSid = 0; $secondarySid = 0 }
        foreach ($rule in @($acl.Access)) {
            if ($rule.IsInherited) { return $false }
            $sid = $rule.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            if ($allowed -notcontains $sid) { return $false }
            if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Deny) { return $false }
            $granted[$sid] = $granted[$sid] -bor [int]$rule.FileSystemRights
        }

        $fullControl = [int][System.Security.AccessControl.FileSystemRights]::FullControl
        if (($granted[$systemSid] -band $fullControl) -ne $fullControl) { return $false }
        if ($Kind -ne 'Fixture') {
            return ($granted[$currentSid] -band $fullControl) -eq $fullControl
        }

        $ownerSid = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        $groupSid = $acl.GetGroup([System.Security.Principal.SecurityIdentifier]).Value
        if ($ownerSid -ne $systemSid -or $groupSid -ne $systemSid) { return $false }

        $fixtureAllowed = [int](
            [System.Security.AccessControl.FileSystemRights]::Delete -bor
            [System.Security.AccessControl.FileSystemRights]::ReadPermissions -bor
            [System.Security.AccessControl.FileSystemRights]::Synchronize
        )
        $administratorRights = $granted[$administratorsSid]
        return ($administratorRights -band [int][System.Security.AccessControl.FileSystemRights]::Delete) -ne 0 -and
            ($administratorRights -band (-bnot $fixtureAllowed)) -eq 0
    }
    catch { return $false }
}

function Get-FixtureState {
    $exists = Test-Path -LiteralPath $Selected.Fixture -PathType Leaf
    $attested = Test-Path -LiteralPath $Selected.Attestation -PathType Leaf
    $safe = $false
    if ($exists -and $attested) {
        $safe = (Test-ProtectedAcl -Path $FixtureRoot -Kind Container) -and
            (Test-ProtectedAcl -Path $Selected.Fixture -Kind Fixture) -and
            (Test-ProtectedAcl -Path $Selected.Attestation -Kind Marker)
    }
    [ordered]@{ Exists = [bool]$exists; Attested = [bool]$attested; AclSafe = [bool]$safe }
}

function Test-VpnRouterClean {
    $guiPath = 'C:\Program Files\VPNRouter\app\VPNRouter.App.exe'
    $corePath = 'C:\ProgramData\VPNRouter\bin\sing-box.exe'
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $guiCount = @($processes | Where-Object { ([string]$_.ExecutablePath) -ieq $guiPath }).Count
    $coreCount = @($processes | Where-Object { ([string]$_.ExecutablePath) -ieq $corePath }).Count
    $tun = Get-NetAdapter -Name 'VPNRouter-TUN' -ErrorAction SilentlyContinue | Select-Object -First 1
    return $guiCount -eq 1 -and $coreCount -eq 0 -and $null -eq $tun
}

function Test-ManagementRoute {
    try {
        $service = Get-Service -Name 'Tailscale' -ErrorAction Stop
        if ($service.Status -ne 'Running') { return $false }
        $adapters = @(Get-NetAdapter -ErrorAction Stop | Where-Object {
            $_.Status -eq 'Up' -and ($_.Name -eq 'Tailscale' -or $_.InterfaceDescription -like 'Tailscale*')
        })
        if (-not $adapters) { return $false }
        $connections = @(Get-NetTCPConnection -State Established -ErrorAction Stop | Where-Object {
            $_.LocalPort -in @(5985, 5986)
        })
        foreach ($connection in $connections) {
            $address = [System.Net.IPAddress]::None
            if (-not [System.Net.IPAddress]::TryParse([string]$connection.RemoteAddress, [ref]$address) -or
                $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { continue }
            $bytes = $address.GetAddressBytes()
            if ($bytes[0] -ne 100 -or ($bytes[1] -band 0xC0) -ne 64) { continue }
            $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop | Select-Object -First 1
            if ($adapters.InterfaceIndex -contains $route.InterfaceIndex) { return $true }
        }
        return $false
    }
    catch { return $false }
}

function Test-NoCgnatAddress {
    param([Parameter(Mandatory = $true)] [int]$InterfaceIndex)
    try {
        foreach ($entry in @(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop)) {
            $address = [System.Net.IPAddress]::Parse([string]$entry.IPAddress).GetAddressBytes()
            if ($address[0] -eq 100 -and ($address[1] -band 0xC0) -eq 64) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-TestClientClean {
    foreach ($identity in @($ProfileMap.Control, $ProfileMap.Target)) {
        if (Get-Service -Name $identity.Service -ErrorAction SilentlyContinue) { return $false }
        if (Get-NetAdapter -Name $identity.Adapter -ErrorAction SilentlyContinue) { return $false }
    }
    $clientProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        ([string]$_.ExecutablePath) -ieq $ClientExe
    })
    $manager = Get-Service -Name 'AmneziaWGManager' -ErrorAction SilentlyContinue
    $watchdog = Get-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue
    return $clientProcesses.Count -eq 0 -and -not ($manager -and $manager.Status -ne 'Stopped') -and -not $watchdog
}

function Test-EndpointRoute {
    try {
        $address = [System.Net.Dns]::GetHostAddresses($EndpointHost) |
            Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
            Select-Object -First 1
        if (-not $address) { return $false }
        $route = Find-NetRoute -RemoteIPAddress $address.IPAddressToString -ErrorAction Stop | Select-Object -First 1
        $adapter = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction Stop
        return $adapter.Name -eq $Selected.Adapter -and $adapter.Status -eq 'Up'
    }
    catch { return $false }
}

function Test-EndpointHealth {
    Add-Type -AssemblyName System.Net.Http
    $http = New-Object System.Net.Http.HttpClient
    $http.Timeout = [TimeSpan]::FromSeconds(20)
    try {
        $response = $http.GetAsync("https://$EndpointHost/health").GetAwaiter().GetResult()
        try { return [int]$response.StatusCode -eq 200 }
        finally { $response.Dispose() }
    }
    catch { return $false }
    finally { $http.Dispose() }
}

function Get-AdapterBytes {
    param([Parameter(Mandatory = $true)] [string]$Name)
    $stats = Get-NetAdapterStatistics -Name $Name -ErrorAction Stop
    return [uint64]$stats.ReceivedBytes + [uint64]$stats.SentBytes
}

function Get-PreflightState {
    $fixture = Get-FixtureState
    $binary = Get-OfficialClientBinaryState
    [ordered]@{
        BinaryExists = [bool]$binary.Exists
        BinaryReady = [bool]$binary.Valid
        FixtureExists = [bool]$fixture.Exists
        FixtureAttested = [bool]$fixture.Attested
        FixtureAclSafe = [bool]$fixture.AclSafe
        VpnRouterClean = Test-VpnRouterClean
        ManagementSafe = Test-ManagementRoute
        ClientClean = Test-TestClientClean
    }
}

function Get-PreflightLifecycle {
    param($State)
    if (-not $State.BinaryExists) { return 'ClientNotInstalled' }
    if (-not $State.BinaryReady) { return 'ClientBinaryInvalid' }
    if (-not $State.FixtureExists) { return 'FixtureMissing' }
    if (-not $State.FixtureAttested) { return 'FixtureAttestationMissing' }
    if (-not $State.FixtureAclSafe) { return 'FixtureAclUnsafe' }
    if (-not $State.VpnRouterClean) { return 'VpnRouterNotClean' }
    if (-not $State.ManagementSafe) { return 'ManagementRouteUnsafe' }
    if (-not $State.ClientClean) { return 'DirtyClientState' }
    return 'Ready'
}

function Invoke-OfficialClientCommand {
    param(
        [Parameter(Mandatory = $true)] [ValidateSet('Install', 'Uninstall')] [string]$Command,
        [Parameter(Mandatory = $true)] $Identity
    )
    if (-not (Test-OfficialClientBinary)) { return -1 }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ClientExe
    $psi.Arguments = if ($Command -eq 'Install') {
        '/installtunnelservice "' + [string]$Identity.Fixture + '"'
    }
    else {
        '/uninstalltunnelservice "' + [string]$Identity.Adapter + '"'
    }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            return -2
        }
        $process.WaitForExit()
        $stdout.GetAwaiter().GetResult() | Out-Null
        $stderr.GetAwaiter().GetResult() | Out-Null
        return [int]$process.ExitCode
    }
    finally { $process.Dispose() }
}

function Stop-FixedTunnel {
    param($Identity)
    try {
        $service = Get-Service -Name $Identity.Service -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -ne 'Stopped') {
                Stop-Service -Name $Identity.Service -Force -ErrorAction SilentlyContinue
            }
            if (Test-OfficialClientBinary) {
                Invoke-OfficialClientCommand -Command Uninstall -Identity $Identity | Out-Null
            }
            if (Get-Service -Name $Identity.Service -ErrorAction SilentlyContinue) {
                & "$env:SystemRoot\System32\sc.exe" delete $Identity.Service 2>&1 | Out-Null
            }
        }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
        do {
            $serviceLeft = $null -ne (Get-Service -Name $Identity.Service -ErrorAction SilentlyContinue)
            $adapterLeft = $null -ne (Get-NetAdapter -Name $Identity.Adapter -ErrorAction SilentlyContinue)
            if (-not $serviceLeft -and -not $adapterLeft) { return $true }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        return $false
    }
    catch { return $false }
}

function Remove-FixedWorkRoot {
    try {
        if (Test-Path -LiteralPath $WorkRoot) {
            $resolved = (Resolve-Path -LiteralPath $WorkRoot).Path
            if ($resolved -ine 'C:\r4review\official-ab\current') { return $false }
            $pending = New-Object 'System.Collections.Generic.Queue[string]'
            $pending.Enqueue($resolved)
            while ($pending.Count -gt 0) {
                $current = $pending.Dequeue()
                $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
                if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
                if ($item.PSIsContainer) {
                    foreach ($child in @(Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop)) {
                        if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
                        if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
                    }
                }
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        return -not (Test-Path -LiteralPath $WorkRoot)
    }
    catch { return $false }
}

function Start-FixedWatchdog {
    if (-not (Test-Path -LiteralPath $WorkRoot)) {
        New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
    }
    if (Get-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue) {
        throw 'A fixed official-client watchdog is already present.'
    }
    @'
$ErrorActionPreference = 'SilentlyContinue'
$exe = 'C:\Program Files\AmneziaWG\amneziawg.exe'
$identities = @(
    @{ Service = 'AmneziaWGTunnel$VPNRouter-AB-AWG-Control'; Tunnel = 'VPNRouter-AB-AWG-Control' },
    @{ Service = 'AmneziaWGTunnel$VPNRouter-AB-AWG'; Tunnel = 'VPNRouter-AB-AWG' }
)
foreach ($identity in $identities) {
    Stop-Service -Name $identity.Service -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $exe) { & $exe /uninstalltunnelservice $identity.Tunnel *> $null }
    if (Get-Service -Name $identity.Service -ErrorAction SilentlyContinue) {
        & "$env:SystemRoot\System32\sc.exe" delete $identity.Service *> $null
    }
}
Set-Content -LiteralPath 'C:\r4review\official-ab\current\watchdog-state.txt' -Value 'Fired' -Encoding Ascii
'@ | Set-Content -LiteralPath $WatchdogScript -Encoding UTF8
    Set-Content -LiteralPath $WatchdogState -Value 'Armed' -Encoding Ascii
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$WatchdogScript`""
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(10)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $WatchdogTask -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
}

function Stop-FixedWatchdog {
    $fired = $false
    try {
        if (Test-Path -LiteralPath $WatchdogState) {
            $fired = ((Get-Content -LiteralPath $WatchdogState -Raw).Trim() -eq 'Fired')
        }
        $task = Get-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue
        if ($task -and $task.State -eq 'Running') {
            Stop-ScheduledTask -TaskName $WatchdogTask -ErrorAction Stop
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
            do {
                Start-Sleep -Milliseconds 250
                $task = Get-ScheduledTask -TaskName $WatchdogTask -ErrorAction SilentlyContinue
            } while ($task -and $task.State -eq 'Running' -and [DateTimeOffset]::UtcNow -lt $deadline)
            if ($task -and $task.State -eq 'Running') { return $false }
        }
        if (Test-Path -LiteralPath $WatchdogState) {
            $fired = $fired -or ((Get-Content -LiteralPath $WatchdogState -Raw).Trim() -eq 'Fired')
        }
        $script:WatchdogFired = $script:WatchdogFired -or $fired
        if ($task) {
            Unregister-ScheduledTask -TaskName $WatchdogTask -Confirm:$false -ErrorAction Stop
        }
        if (-not $fired -and (Test-Path -LiteralPath $WorkRoot)) {
            Set-Content -LiteralPath $WatchdogState -Value 'Disarmed' -Encoding Ascii
        }
        return -not $script:WatchdogFired
    }
    catch { return $false }
}

function Invoke-FixedCleanup {
    $controlClean = Stop-FixedTunnel -Identity $ProfileMap.Control
    $targetClean = Stop-FixedTunnel -Identity $ProfileMap.Target
    $watchdogClean = Stop-FixedWatchdog
    $stateClean = Test-TestClientClean
    $managementSafe = Test-ManagementRoute
    $vpnRouterClean = Test-VpnRouterClean
    $rootClean = Remove-FixedWorkRoot
    $script:LastCleanupStateClean = $controlClean -and $targetClean -and $stateClean -and
        $managementSafe -and $vpnRouterClean -and $rootClean
    return $script:LastCleanupStateClean -and $watchdogClean
}

function Invoke-Install {
    if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
        return New-Result -Status BLOCKED -Lifecycle InstallerNotApproved
    }
    if ((Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedMsiSha256) {
        return New-Result -Status BLOCKED -Lifecycle InstallerNotApproved
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $MsiPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $ExpectedSignerThumbprint) {
        return New-Result -Status BLOCKED -Lifecycle InstallerNotApproved
    }
    if (-not (Test-VpnRouterClean)) { return New-Result -Status BLOCKED -Lifecycle VpnRouterNotClean }
    if (-not (Test-ManagementRoute)) { return New-Result -Status BLOCKED -Lifecycle ManagementRouteUnsafe }
    if (-not (Test-TestClientClean)) { return New-Result -Status BLOCKED -Lifecycle DirtyClientState }

    $installer = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @(
        '/i', $MsiPath, '/qn', '/norestart', 'DO_NOT_LAUNCH=1'
    ) -WindowStyle Hidden -Wait -PassThru
    $installer.Refresh()
    if ($installer.ExitCode -ne 0 -or -not (Test-OfficialClientBinary)) {
        return New-Result -Status BLOCKED -Lifecycle InstallFailed
    }
    $rootClean = Remove-FixedWorkRoot
    if (-not $rootClean) { return New-Result -Status ABORTED -Lifecycle CleanupFailed }
    return New-Result -Status PASS -Lifecycle Installed -ManagementRouteIntact $true -CleanTeardown $true
}

function Invoke-FixedPayload {
    if (-not (Test-Path -LiteralPath $PayloadZip -PathType Leaf)) {
        return [ordered]@{ Success = $false; Lifecycle = 'PayloadNotApproved' }
    }
    if ((Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedPayloadSha256) {
        return [ordered]@{ Success = $false; Lifecycle = 'PayloadHashMismatch' }
    }
    if (Test-Path -LiteralPath $PayloadRoot) { Remove-Item -LiteralPath $PayloadRoot -Recurse -Force }
    Expand-Archive -LiteralPath $PayloadZip -DestinationPath $PayloadRoot -Force
    if (-not (Test-Path -LiteralPath $PayloadExe -PathType Leaf)) {
        return [ordered]@{ Success = $false; Lifecycle = 'PayloadMissing' }
    }

    $process = $null
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $PayloadExe
        $psi.WorkingDirectory = $PayloadRoot
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(330000)) {
            $process.Kill()
            return [ordered]@{ Success = $false; Lifecycle = 'PayloadTimeout' }
        }
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult()
        $stderr.GetAwaiter().GetResult() | Out-Null
        if ([string]::IsNullOrWhiteSpace($output)) {
            return [ordered]@{ Success = $false; Lifecycle = 'PayloadOutputEmpty' }
        }
        try { $metrics = $output | ConvertFrom-Json }
        catch {
            if ($process.ExitCode -ne 0) { return [ordered]@{ Success = $false; Lifecycle = 'PayloadExitNonZero' } }
            return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
        }
        if ($process.ExitCode -ne 0) { return [ordered]@{ Success = $false; Lifecycle = 'PayloadExitNonZero' } }
        $lifecycle = [string]$metrics.Status
        if ($lifecycle -notin @('Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure', 'InternalFailure')) {
            return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
        }
        $cleanMetrics = [ordered]@{}
        foreach ($name in @('Sent', 'Received', 'Loss', 'Duplicate', 'Reorder', 'Corruption', 'Unknown')) {
            $property = $metrics.PSObject.Properties[$name]
            if ($null -eq $property -or $property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
                return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
            }
            try { $value = [int64]$property.Value }
            catch { return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' } }
            if ($value -lt 0 -or [decimal]$value -ne [decimal]$property.Value) {
                return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
            }
            $cleanMetrics[$name] = $value
        }
        foreach ($name in @('RttP50Ms', 'RttP95Ms', 'RttP99Ms', 'MaxAcknowledgedGapMs')) {
            $property = $metrics.PSObject.Properties[$name]
            if ($null -eq $property -or $property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
                return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
            }
            try { $value = [double]$property.Value }
            catch { return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' } }
            if ($value -lt 0 -or [double]::IsNaN($value) -or [double]::IsInfinity($value)) {
                return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
            }
            $cleanMetrics[$name] = $value
        }
        if ($cleanMetrics.Received -gt $cleanMetrics.Sent -or
            $cleanMetrics.Loss -ne ($cleanMetrics.Sent - $cleanMetrics.Received) -or
            $cleanMetrics.RttP50Ms -gt $cleanMetrics.RttP95Ms -or
            $cleanMetrics.RttP95Ms -gt $cleanMetrics.RttP99Ms) {
            return [ordered]@{ Success = $false; Lifecycle = 'PayloadResultInvalid' }
        }
        return [ordered]@{
            Success = $true
            Lifecycle = $lifecycle
            Metrics = $cleanMetrics
        }
    }
    finally {
        if ($process -and -not $process.HasExited) { try { $process.Kill() } catch { } }
        if ($process) { $process.Dispose() }
    }
}

function Invoke-Cycle {
    $startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $preflight = Get-PreflightState
    $preflightLifecycle = Get-PreflightLifecycle -State $preflight
    if ($preflightLifecycle -ne 'Ready') {
        return New-Result -Status BLOCKED -Lifecycle $preflightLifecycle -StartedUtc $startedUtc -ManagementRouteIntact ([bool]$preflight.ManagementSafe) -CleanTeardown ([bool]$preflight.ClientClean)
    }
    if (-not (Test-Path -LiteralPath $PayloadZip -PathType Leaf)) {
        return New-Result -Status BLOCKED -Lifecycle PayloadNotApproved -StartedUtc $startedUtc -ManagementRouteIntact $true -CleanTeardown $true
    }

    $cleanup = $false
    $result = $null
    try {
        do {
            Start-FixedWatchdog
            if ((Invoke-OfficialClientCommand -Command Install -Identity $Selected) -ne 0) {
                $result = New-Result -Status BLOCKED -Lifecycle TunnelStateUnavailable -StartedUtc $startedUtc -ManagementRouteIntact $true
                break
            }

            $adapter = $null
            $service = $null
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
            do {
                $service = Get-Service -Name $Selected.Service -ErrorAction SilentlyContinue
                $adapter = Get-NetAdapter -Name $Selected.Adapter -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($service -and $service.Status -eq 'Running' -and $adapter -and $adapter.Status -eq 'Up') { break }
                Start-Sleep -Seconds 1
            } while ([DateTimeOffset]::UtcNow -lt $deadline)
            if (-not $service -or $service.Status -ne 'Running' -or -not $adapter -or $adapter.Status -ne 'Up') {
                $result = New-Result -Status BLOCKED -Lifecycle TunnelStateUnavailable -StartedUtc $startedUtc -ManagementRouteIntact (Test-ManagementRoute)
                break
            }
            if (-not (Test-NoCgnatAddress -InterfaceIndex $adapter.InterfaceIndex) -or -not (Test-ManagementRoute)) {
                $result = New-Result -Status ABORTED -Lifecycle ManagementRouteUnsafe -StartedUtc $startedUtc
                break
            }

            $route = Test-EndpointRoute
            if (-not $route) {
                $result = New-Result -Status BLOCKED -Lifecycle RouteNotTunnel -StartedUtc $startedUtc -ManagementRouteIntact $true
                break
            }
            if (-not (Test-EndpointHealth)) {
                $result = New-Result -Status BLOCKED -Lifecycle EndpointUnavailable -StartedUtc $startedUtc -ManagementRouteIntact $true -ExpectedAdapterRoute $true
                break
            }

            $quietStart = Get-AdapterBytes -Name $Selected.Adapter
            Start-Sleep -Seconds 5
            $quietEnd = Get-AdapterBytes -Name $Selected.Adapter
            $payload = Invoke-FixedPayload
            $loadEnd = Get-AdapterBytes -Name $Selected.Adapter
            $quietDelta = if ($quietEnd -ge $quietStart) { [uint64]($quietEnd - $quietStart) } else { [uint64]0 }
            $loadDelta = if ($loadEnd -ge $quietEnd) { [uint64]($loadEnd - $quietEnd) } else { [uint64]0 }
            $tunCorrelation = $loadDelta -gt 0 -and $loadDelta -gt $quietDelta

            if (-not $payload.Success) {
                $result = New-Result -Status ABORTED -Lifecycle ([string]$payload.Lifecycle) -StartedUtc $startedUtc -ManagementRouteIntact $true -ExpectedAdapterRoute $true -AdapterByteCorrelation $tunCorrelation
                break
            }
            $routeAfter = Test-EndpointRoute
            $serviceAfter = Get-Service -Name $Selected.Service -ErrorAction SilentlyContinue
            $adapterAfter = Get-NetAdapter -Name $Selected.Adapter -ErrorAction SilentlyContinue | Select-Object -First 1
            $managementAfter = Test-ManagementRoute
            $stable = $routeAfter -and $serviceAfter -and $serviceAfter.Status -eq 'Running' -and
                $adapterAfter -and $adapterAfter.Status -eq 'Up' -and $managementAfter -and
                (Test-NoCgnatAddress -InterfaceIndex $adapterAfter.InterfaceIndex) -and (Test-VpnRouterClean)
            if (-not $stable -or -not $tunCorrelation) {
                $result = New-Result -Status ABORTED -Lifecycle TunnelStateUnavailable -StartedUtc $startedUtc -ManagementRouteIntact $managementAfter -ExpectedAdapterRoute ([bool]$routeAfter) -AdapterByteCorrelation $tunCorrelation -Metrics $payload.Metrics
                break
            }

            $lifecycle = [string]$payload.Lifecycle
            if ($payload.Metrics.Corruption -gt 0 -or $payload.Metrics.Unknown -gt 0) {
                $result = New-Result -Status ABORTED -Lifecycle PayloadIntegrityFailure -StartedUtc $startedUtc -ManagementRouteIntact $true -ExpectedAdapterRoute $true -AdapterByteCorrelation $true -Metrics $payload.Metrics
                break
            }
            if ($lifecycle -eq 'InternalFailure') {
                $result = New-Result -Status ABORTED -Lifecycle InternalFailure -StartedUtc $startedUtc -ManagementRouteIntact $true -ExpectedAdapterRoute $true -AdapterByteCorrelation $true -Metrics $payload.Metrics
                break
            }
            if ($lifecycle -eq 'Completed' -and $payload.Metrics.Received -le 0) {
                $result = New-Result -Status ABORTED -Lifecycle PayloadResultInvalid -StartedUtc $startedUtc -ManagementRouteIntact $true -ExpectedAdapterRoute $true -AdapterByteCorrelation $true -Metrics $payload.Metrics
                break
            }
            $result = New-Result -Status $(if ($lifecycle -eq 'Completed') { 'PASS' } else { 'FAIL' }) -Lifecycle $lifecycle -StartedUtc $startedUtc -ManagementRouteIntact $true -ExpectedAdapterRoute $true -AdapterByteCorrelation $true -Metrics $payload.Metrics
        } while ($false)
    }
    catch {
        $result = New-Result -Status ABORTED -Lifecycle InternalFailure -StartedUtc $startedUtc -ManagementRouteIntact (Test-ManagementRoute)
    }
    finally {
        $cleanup = Invoke-FixedCleanup
    }
    if (-not $cleanup) {
        $metrics = if ($result -and $result.Contains('Metrics')) { $result.Metrics } else { @{} }
        $failure = if ($script:WatchdogFired) { 'WatchdogFired' } else { 'CleanupFailed' }
        return New-Result -Status ABORTED -Lifecycle $failure -StartedUtc $startedUtc -ManagementRouteIntact (Test-ManagementRoute) -CleanTeardown $script:LastCleanupStateClean -Metrics $metrics
    }
    if (-not $result) {
        return New-Result -Status ABORTED -Lifecycle InternalFailure -StartedUtc $startedUtc -ManagementRouteIntact (Test-ManagementRoute) -CleanTeardown $true
    }
    $result.CleanTeardown = $true
    $result.EndedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    return $result
}

try {
    $SemaphoreHeld = $Semaphore.WaitOne(0)
    if (-not $SemaphoreHeld) {
        New-Result -Status ABORTED -Lifecycle DirtyClientState | ConvertTo-Json -Depth 5 -Compress
        exit
    }

    $result = switch ($Operation) {
        'Preflight' {
            $state = Get-PreflightState
            $lifecycle = Get-PreflightLifecycle -State $state
            New-Result -Status $(if ($lifecycle -eq 'Ready') { 'PASS' } else { 'BLOCKED' }) -Lifecycle $lifecycle -ManagementRouteIntact $state.ManagementSafe -CleanTeardown $state.ClientClean
        }
        'Install' { Invoke-Install }
        'Cycle' { Invoke-Cycle }
        'Cleanup' {
            $clean = Invoke-FixedCleanup
            $lifecycle = if ($clean) { 'Cleaned' } elseif ($script:WatchdogFired) { 'WatchdogFired' } else { 'CleanupFailed' }
            New-Result -Status $(if ($clean) { 'PASS' } else { 'ABORTED' }) -Lifecycle $lifecycle -ManagementRouteIntact (Test-ManagementRoute) -CleanTeardown $script:LastCleanupStateClean
        }
    }
    $result | ConvertTo-Json -Depth 5 -Compress
}
catch {
    New-Result -Status ABORTED -Lifecycle InternalFailure | ConvertTo-Json -Depth 5 -Compress
}
finally {
    if ($SemaphoreHeld) { $Semaphore.Release() | Out-Null }
    $Semaphore.Dispose()
}
