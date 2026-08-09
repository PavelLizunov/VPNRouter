# Orchestrates fixed-WINBRAT stability checks exclusively through
# tools/brat-verify.ps1. This file must never contain WinRM, process, route,
# UI Automation or screen-capture implementation.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ColdCycles', 'Soak', 'ProtocolLoad', 'BrowserLoad', 'Cleanup')]
    [string]$Mode,

    [ValidatePattern('^[0-9A-Za-z.-]{1,32}$')]
    [string]$Version,

    [ValidateRange(1, 50)]
    [int]$Cycles = 10,

    [ValidateRange(1, 1440)]
    [int]$DurationMinutes = 120,

    [ValidateRange(5, 300)]
    [int]$SampleSeconds = 15,

    [ValidateSet('VlessReality', 'VlessWebSocket', 'VlessXhttp', 'Hysteria2', 'Tuic', 'AmneziaWG', 'Naive', 'DnsTunnel', 'Shadowsocks')]
    [string]$ProtocolClass,

    [ValidateRange(0, 20)]
    [int]$ProtocolOrdinal = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VerifyScript = Join-Path $PSScriptRoot 'brat-verify.ps1'
$EvidenceRoot = Join-Path $Root 'artifacts\brat-stability'
$RunStartedUtc = [DateTimeOffset]::UtcNow
$RunId = $RunStartedUtc.ToString('yyyyMMdd-HHmmss')
$EvidencePath = Join-Path $EvidenceRoot "$RunId-$Mode.jsonl"
$DataPlaneBlocked = $false
$RunFailure = $null
$CleanupFailure = $null
$MeasuredFailureCount = 0
$Mutex = New-Object System.Threading.Mutex($false, 'Local\VPNRouterBratStability')
$MutexHeld = $false

if ($Mode -ne 'Cleanup' -and [string]::IsNullOrWhiteSpace($Version)) {
    throw '-Version is required for non-cleanup evidence.'
}
if ($Mode -eq 'ProtocolLoad' -and [string]::IsNullOrWhiteSpace($ProtocolClass)) {
    throw '-ProtocolClass is required for ProtocolLoad.'
}
if (-not (Test-Path $VerifyScript)) { throw 'tools/brat-verify.ps1 is missing.' }

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)] [string]$Kind,
        [Parameter(Mandatory = $false)] $Data
    )
    if (-not (Test-Path $EvidenceRoot)) {
        New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    }
    $record = [ordered]@{
        AtUtc   = [DateTimeOffset]::UtcNow.ToString('o')
        RunId   = $RunId
        Mode    = $Mode
        Version = if ($Version) { $Version } else { 'installed' }
        Kind    = $Kind
        Data    = $Data
    }
    Add-Content -LiteralPath $EvidencePath -Value ($record | ConvertTo-Json -Depth 8 -Compress) -Encoding UTF8
}

function Invoke-BratVerify {
    param([Parameter(Mandatory = $true)] [hashtable]$Arguments)
    & $VerifyScript @Arguments
}

function Invoke-BratVerifyJson {
    param([Parameter(Mandatory = $true)] [hashtable]$Arguments)
    $raw = @(Invoke-BratVerify -Arguments $Arguments)
    $text = ($raw -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { throw 'Remote verifier returned no JSON.' }
    try { return $text | ConvertFrom-Json }
    catch { throw 'Remote verifier returned invalid JSON.' }
}

function Get-BratState {
    Invoke-BratVerifyJson -Arguments @{ Action = 'state' }
}

function Test-CleanState {
    param($State)
    return [int]$State.GuiCount -eq 1 -and
        [int]$State.CoreCount -eq 0 -and
        [string]$State.TunState -ne 'Up'
}

function Test-ConnectedState {
    param($State)
    return [int]$State.GuiCount -eq 1 -and
        [int]$State.CoreCount -eq 1 -and
        [string]$State.TunState -eq 'Up'
}

function Wait-BratState {
    param(
        [Parameter(Mandatory = $true)] [ValidateSet('Clean', 'Connected')] [string]$Expected,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
    )
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $state = Get-BratState
        $matches = if ($Expected -eq 'Clean') { Test-CleanState $state } else { Test-ConnectedState $state }
        if ($matches) { return $state }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "WINBRAT did not reach the expected $Expected state within the bounded wait."
}

function Get-CtaNames {
    param([Parameter(Mandatory = $true)] [ValidateSet('Connect', 'Disconnect')] [string]$Target)
    # Keep the script ASCII-safe for Windows PowerShell 5.1 while still
    # matching both localized button names exposed through UI Automation.
    $codePoints = if ($Target -eq 'Connect') {
        @(0x041F, 0x043E, 0x0434, 0x043A, 0x043B, 0x044E, 0x0447, 0x0438, 0x0442, 0x044C)
    }
    else {
        @(0x041E, 0x0442, 0x043A, 0x043B, 0x044E, 0x0447, 0x0438, 0x0442, 0x044C)
    }
    $localized = -join @($codePoints | ForEach-Object { [char]$_ })
    if ($Target -eq 'Connect') {
        $advancedRuCodePoints = @(
            0x0417, 0x0430, 0x043F, 0x0443, 0x0441, 0x0442, 0x0438, 0x0442, 0x044C,
            0x20, 0x56, 0x50, 0x4E)
        $advancedRuText = -join @($advancedRuCodePoints | ForEach-Object { [char]$_ })
        $advancedEn = ([char]0x25B6) + '  Start VPN'
        $advancedRu = ([char]0x25B6) + '  ' + $advancedRuText
    }
    else {
        $advancedRuCodePoints = @(
            0x041E, 0x0441, 0x0442, 0x0430, 0x043D, 0x043E, 0x0432, 0x0438, 0x0442, 0x044C,
            0x20, 0x56, 0x50, 0x4E)
        $advancedRuText = -join @($advancedRuCodePoints | ForEach-Object { [char]$_ })
        $advancedEn = ([char]0x2B1B) + '  Stop VPN'
        $advancedRu = ([char]0x2B1B) + '  ' + $advancedRuText
    }
    return "$Target||$localized||$advancedEn||$advancedRu"
}

function Invoke-Cta {
    param([Parameter(Mandatory = $true)] [ValidateSet('Connect', 'Disconnect')] [string]$Target)
    $names = Get-CtaNames $Target
    Invoke-BratVerify -Arguments @{
        Action         = 'uia'
        Name           = $names
        ControlType    = 'Button'
        UiaOperation   = 'Invoke'
        TimeoutSeconds = 10
    } | Out-Null
}

function Assert-Cta {
    param([Parameter(Mandatory = $true)] [ValidateSet('Connect', 'Disconnect')] [string]$Target)
    $names = Get-CtaNames $Target
    Invoke-BratVerify -Arguments @{
        Action         = 'uia'
        Name           = $names
        ControlType    = 'Button'
        UiaOperation   = 'Inspect'
        TimeoutSeconds = 10
    } | Out-Null
}

function Ensure-Disconnected {
    $state = Get-BratState
    if ([int]$state.GuiCount -ne 1) { throw 'Cleanup requires exactly one owned VPNRouter GUI.' }
    if ([int]$state.CoreCount -eq 0 -and [string]$state.TunState -eq 'Up') {
        throw 'Cleanup found an Up TUN without an owned core and will not guess at recovery.'
    }
    if ([int]$state.CoreCount -gt 0) { Invoke-Cta -Target Disconnect }
    $clean = Wait-BratState -Expected Clean -TimeoutSeconds 30
    Assert-Cta -Target Connect
    Write-Evidence -Kind 'CleanState' -Data $clean
    return $clean
}

function Connect-And-Wait {
    $before = Get-BratState
    if (-not (Test-CleanState $before)) { throw 'Connect requires a clean starting state.' }
    Write-Evidence -Kind 'BeforeConnect' -Data $before
    Invoke-Cta -Target Connect
    $connected = Wait-BratState -Expected Connected -TimeoutSeconds 120
    Assert-Cta -Target Disconnect
    Write-Evidence -Kind 'ConnectedState' -Data $connected
    return $connected
}

function Invoke-Probe {
    param([Parameter(Mandatory = $true)] [ValidateSet('Control', 'Boundary')] [string]$Profile)
    $result = Invoke-BratVerifyJson -Arguments @{
        Action         = 'probe'
        ProbeProfile   = $Profile
        TimeoutSeconds = 8
    }
    Write-Evidence -Kind "${Profile}Probe" -Data $result
    return $result
}

function Get-Lifecycle {
    param([Parameter(Mandatory = $true)] [DateTimeOffset]$Since)
    $result = Invoke-BratVerifyJson -Arguments @{
        Action   = 'lifecycle'
        SinceUtc = $Since.ToString('o')
    }
    Write-Evidence -Kind 'Lifecycle' -Data $result
    return $result
}

function Open-SubscribePage {
    # Protocol selection lives only in Advanced mode. The first Invoke may
    # fail when the page is already Advanced; that is an expected idempotent
    # probe, followed by a semantic tab selection and SubList assertion.
    try {
        Invoke-BratVerify -Arguments @{
            Action = 'uia'; Name = 'Advanced settings'; ControlType = 'Button'
            UiaOperation = 'Invoke'; TimeoutSeconds = 5
        } | Out-Null
    }
    catch { }
    Invoke-BratVerify -Arguments @{
        Action = 'uia'; Name = 'Subscribe'; ControlType = 'ListItem'
        UiaOperation = 'Select'; TimeoutSeconds = 10
    } | Out-Null
    Invoke-BratVerify -Arguments @{
        Action = 'uia'; AutomationId = 'SubList'; UiaOperation = 'Inspect'; TimeoutSeconds = 10
    } | Out-Null
}

function Switch-ToSimplePage {
    Invoke-BratVerify -Arguments @{
        Action = 'uia'; Name = ([char]0x25C2) + ' Simple'; ControlType = 'Button'
        UiaOperation = 'Invoke'; TimeoutSeconds = 10
    } | Out-Null
    Invoke-BratVerify -Arguments @{
        Action = 'uia'; Name = 'All traffic'; ControlType = 'RadioButton'
        UiaOperation = 'Inspect'; TimeoutSeconds = 10
    } | Out-Null
}

function Select-ProtocolRow {
    $raw = @(Invoke-BratVerify -Arguments @{
        Action          = 'uia'
        AutomationId    = 'SubList'
        UiaOperation    = 'SelectProtocol'
        ProtocolClass   = $ProtocolClass
        ProtocolOrdinal = $ProtocolOrdinal
        TimeoutSeconds  = 25
    })
    $line = ($raw | Select-String -Pattern '^\{"ProtocolClass"').Line | Select-Object -Last 1
    if (-not $line) { throw 'Protocol selector returned no safe coordinate JSON.' }
    try { $selected = $line | ConvertFrom-Json }
    catch { throw 'Protocol selector returned invalid safe coordinate JSON.' }
    if ([string]$selected.ProtocolClass -ne $ProtocolClass -or
        [int]$selected.Ordinal -ne $ProtocolOrdinal -or
        [int]$selected.AbsoluteOrdinal -lt 0) {
        throw 'Protocol selector returned a mismatched safe coordinate.'
    }
    Write-Evidence -Kind 'ProtocolSelected' -Data $selected
    return $selected
}

function Invoke-GameUdpLoad {
    $result = Invoke-BratVerifyJson -Arguments @{
        Action = 'loadtest'
        LoadProfile = 'GameUdp'
    }
    if ([string]$result.Status -notin @('PASS', 'FAIL', 'BLOCKED')) {
        throw 'Load verifier returned an invalid status.'
    }
    if ([string]$result.Lifecycle -notin @(
        'PayloadNotApproved', 'EndpointUnavailable', 'MeasurementGated',
        'Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure', 'InternalFailure',
        'PayloadHashMismatch', 'PayloadMissing', 'PayloadTimeout', 'PayloadExitNonZero',
        'PayloadOutputMissing', 'PayloadOutputEmpty', 'PayloadFailed', 'PayloadResultInvalid')) {
        throw 'Load verifier returned an invalid lifecycle enum.'
    }
    Write-Evidence -Kind 'GameUdp' -Data $result
    return $result
}

function Invoke-BrowserBurstLoad {
    $result = Invoke-BratVerifyJson -Arguments @{
        Action = 'loadtest'
        LoadProfile = 'BrowserBurst'
    }
    if ([string]$result.Status -notin @('PASS', 'FAIL', 'BLOCKED')) {
        throw 'Browser load verifier returned an invalid status.'
    }
    if ([string]$result.Lifecycle -notin @(
        'PayloadNotApproved', 'EndpointUnavailable', 'FullTunnelNotProven', 'BrowserMissing', 'BrowserSignatureUnverified',
        'RouteNotTunnel', 'TunnelStateUnavailable', 'MeasurementGated', 'Completed',
        'PayloadHashMismatch', 'PayloadMissing', 'PayloadTimeout', 'PayloadExitNonZero',
        'PayloadOutputMissing', 'PayloadFailed', 'PayloadResultInvalid',
        'BrowserProcessNotProven', 'TunCorrelationNotProven',
        'BrowserProbeInputRejected', 'BrowserProbeAlreadyRunning', 'BrowserProbePlatformUnsupported', 'BrowserProbeBrowserMissing',
        'BrowserProbeEdgeLaunchFailed', 'BrowserProbeBrowserExited', 'BrowserProbeDevToolsUnavailable', 'BrowserProbePageUnavailable',
        'BrowserProbePagePollingFailure', 'BrowserProbeDevToolsFailure', 'BrowserProbeInvalidPageState', 'BrowserProbeTimedOut', 'BrowserProbeInternalFailure',
        'BrowserProbeCleanupFailure', 'BrowserProbeLifecycleUnrecognized')) {
        throw 'Browser load verifier returned an invalid lifecycle enum.'
    }
    Write-Evidence -Kind 'BrowserBurst' -Data $result
    return $result
}

function Invoke-BrowserLoad {
    $script:DataPlaneBlocked = $false
    Ensure-Disconnected | Out-Null
    for ($cycle = 1; $cycle -le 3; $cycle++) {
        $cycleStarted = [DateTimeOffset]::UtcNow
        Write-Evidence -Kind 'BrowserCycleStarted' -Data ([ordered]@{ Cycle = $cycle })
        $connected = Connect-And-Wait
        if ([string]$connected.RouteScope -ne 'Tunnel') {
            throw 'Browser load requires the fixed owned route to be Tunnel.'
        }

        $load = Invoke-BrowserBurstLoad
        if ([string]$load.Status -eq 'BLOCKED') {
            throw 'Browser load lost payload, endpoint, full-tunnel or workload attribution.'
        }
        if ([string]$load.Lifecycle -ne 'Completed') {
            throw 'Browser load encountered a harness-integrity failure rather than a measurement.'
        }

        $held = Get-BratState
        $stillConnected = Test-ConnectedState $held
        Write-Evidence -Kind 'BrowserPostLoadState' -Data $held
        $lifecycle = Get-Lifecycle -Since $cycleStarted
        Ensure-Disconnected | Out-Null

        $cyclePassed = [string]$load.Status -eq 'PASS' -and $stillConnected -and
            [int]$lifecycle.FatalCount -eq 0 -and [int]$lifecycle.UnknownErrorCount -eq 0
        if (-not $cyclePassed) { $script:MeasuredFailureCount++ }
        Write-Evidence -Kind 'BrowserCycleResult' -Data ([ordered]@{
            Cycle = $cycle
            Result = if ($cyclePassed) { 'PASS' } else { 'MEASURED_FAILURE' }
            LoadStatus = [string]$load.Status
            Lifecycle = [string]$load.Lifecycle
            StayedConnected = [bool]$stillConnected
        })
    }
}

function Invoke-ProtocolLoad {
    Ensure-Disconnected | Out-Null
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $cycleStarted = [DateTimeOffset]::UtcNow
        Write-Evidence -Kind 'ProtocolCycleStarted' -Data ([ordered]@{
            ProtocolClass = $ProtocolClass
            ProtocolOrdinal = $ProtocolOrdinal
            Repeat = $cycle
        })

        Open-SubscribePage
        Select-ProtocolRow | Out-Null
        Switch-ToSimplePage
        $connected = Connect-And-Wait
        if ([string]$connected.RouteScope -ne 'Tunnel') {
            throw 'Protocol load requires the fixed probe route to be Tunnel.'
        }

        $load = Invoke-GameUdpLoad
        if ([string]$load.Status -eq 'BLOCKED') {
            throw 'Protocol load lost payload, endpoint, full-tunnel or route attribution.'
        }
        if ([string]$load.Lifecycle -notin @('Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure')) {
            throw 'Protocol load encountered a harness-integrity failure rather than a network measurement.'
        }

        $held = Get-BratState
        $stillConnected = Test-ConnectedState $held
        Write-Evidence -Kind 'ProtocolPostLoadState' -Data $held
        Ensure-Disconnected | Out-Null
        $lifecycle = Get-Lifecycle -Since $cycleStarted

        $cyclePassed = [string]$load.Status -eq 'PASS' -and
            $stillConnected -and
            [int]$lifecycle.FatalCount -eq 0 -and
            [int]$lifecycle.UnknownErrorCount -eq 0
        if (-not $cyclePassed) { $script:MeasuredFailureCount++ }
        Write-Evidence -Kind 'ProtocolCycleResult' -Data ([ordered]@{
            ProtocolClass = $ProtocolClass
            ProtocolOrdinal = $ProtocolOrdinal
            Repeat = $cycle
            Result = if ($cyclePassed) { 'PASS' } else { 'MEASURED_FAILURE' }
            LoadStatus = [string]$load.Status
            Lifecycle = [string]$load.Lifecycle
            StayedConnected = [bool]$stillConnected
        })
    }
}

function Invoke-ColdCycles {
    $script:DataPlaneBlocked = $false
    Ensure-Disconnected | Out-Null
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $cycleStarted = [DateTimeOffset]::UtcNow
        Write-Evidence -Kind 'CycleStarted' -Data ([ordered]@{ Cycle = $cycle })
        $connected = Connect-And-Wait

        if ([string]$connected.RouteScope -eq 'Tunnel') {
            $probe = Invoke-Probe -Profile Boundary
            if (-not [bool]$probe.Success) { throw "Cold cycle $cycle boundary probe failed." }
        }
        else {
            $script:DataPlaneBlocked = $true
            Write-Evidence -Kind 'DataPlaneBlocked' -Data ([ordered]@{
                Cycle = $cycle; RouteScope = [string]$connected.RouteScope
            })
        }

        Start-Sleep -Seconds 30
        $held = Get-BratState
        if (-not (Test-ConnectedState $held)) { throw "Cold cycle $cycle disconnected during the hold window." }
        Write-Evidence -Kind 'HeldState' -Data $held

        Ensure-Disconnected | Out-Null
        $lifecycle = Get-Lifecycle -Since $cycleStarted
        if ([int]$lifecycle.FatalCount -gt 0 -or [int]$lifecycle.UnknownErrorCount -gt 0) {
            throw "Cold cycle $cycle produced unclassified error-level lifecycle events."
        }
        Write-Evidence -Kind 'CyclePassed' -Data ([ordered]@{ Cycle = $cycle })
    }
}

function Invoke-Soak {
    $script:DataPlaneBlocked = $false
    Ensure-Disconnected | Out-Null
    $soakStarted = [DateTimeOffset]::UtcNow
    $connected = Connect-And-Wait
    $routeIsTunnel = [string]$connected.RouteScope -eq 'Tunnel'
    if ($routeIsTunnel) {
        $boundary = Invoke-Probe -Profile Boundary
        if (-not [bool]$boundary.Success) { throw 'Initial boundary probe failed.' }
    }
    else {
        $script:DataPlaneBlocked = $true
        Write-Evidence -Kind 'DataPlaneBlocked' -Data ([ordered]@{ RouteScope = [string]$connected.RouteScope })
    }

    $deadline = $soakStarted.AddMinutes($DurationMinutes)
    $consecutiveHttpOnly = 0
    $sample = 0
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $sample++
        $state = Get-BratState
        if (-not (Test-ConnectedState $state)) { throw "Soak disconnected unexpectedly at sample $sample." }

        if ($routeIsTunnel) {
            $control = Invoke-Probe -Profile Control
            $httpOk = [bool]$control.Http.Success
            $udpOk = [bool]$control.Udp[0].Success
            $pair = if ($httpOk -and $udpOk) { 'HH' }
                elseif ($httpOk) { 'HNotU' }
                elseif ($udpOk) { 'NotHU' }
                else { 'NotHNotU' }
            Write-Evidence -Kind 'SoakSample' -Data ([ordered]@{
                Sample = $sample; Pair = $pair; State = $state
            })
            if ($pair -eq 'HNotU') { $consecutiveHttpOnly++ } else { $consecutiveHttpOnly = 0 }
            if ($consecutiveHttpOnly -eq 3) {
                Write-Evidence -Kind 'UdpDivergenceIncident' -Data ([ordered]@{ Sample = $sample; Consecutive = 3 })
            }
        }
        else {
            Write-Evidence -Kind 'SoakLifecycleSample' -Data ([ordered]@{ Sample = $sample; State = $state })
        }
        Start-Sleep -Seconds $SampleSeconds
    }

    if ($routeIsTunnel) {
        $boundary = Invoke-Probe -Profile Boundary
        if (-not [bool]$boundary.Success) { throw 'Final boundary probe failed.' }
    }
    Ensure-Disconnected | Out-Null
    $lifecycle = Get-Lifecycle -Since $soakStarted
    if ([int]$lifecycle.FatalCount -gt 0 -or [int]$lifecycle.UnknownErrorCount -gt 0) {
        throw 'Soak produced unclassified error-level lifecycle events.'
    }
}

try {
    $MutexHeld = $Mutex.WaitOne(0)
    if (-not $MutexHeld) { throw 'Another WINBRAT stability run is active.' }
    Write-Evidence -Kind 'RunStarted' -Data ([ordered]@{
        Cycles = $Cycles; DurationMinutes = $DurationMinutes; SampleSeconds = $SampleSeconds
        ProtocolClass = $ProtocolClass; ProtocolOrdinal = $ProtocolOrdinal
    })

    switch ($Mode) {
        'ColdCycles' { Invoke-ColdCycles }
        'Soak'       { Invoke-Soak }
        'ProtocolLoad' { Invoke-ProtocolLoad }
        'BrowserLoad' { Invoke-BrowserLoad }
        'Cleanup'    { Ensure-Disconnected | Out-Null }
    }
}
catch {
    $RunFailure = $_
    Write-Evidence -Kind 'RunFailed' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
}
finally {
    if ($MutexHeld -and $Mode -ne 'Cleanup') {
        try { Ensure-Disconnected | Out-Null }
        catch {
            $CleanupFailure = $_
            Write-Evidence -Kind 'CleanupFailed' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
        }
    }
    if ($MutexHeld) { [void]$Mutex.ReleaseMutex() }
    $Mutex.Dispose()
}

$status = if ($RunFailure -or $CleanupFailure) { 'FAIL' }
    elseif ($MeasuredFailureCount -gt 0) { 'MEASURED_FAILURES' }
    elseif ($DataPlaneBlocked) { 'LIFECYCLE_PASS_DATAPLANE_BLOCKED' }
    else { 'PASS' }
$summary = [ordered]@{
    Status = $status
    Mode = $Mode
    MeasuredFailures = $MeasuredFailureCount
    Evidence = $EvidencePath.Substring($Root.Length).TrimStart('\')
}
if (-not $RunFailure -and -not $CleanupFailure -and -not $DataPlaneBlocked) {
    Write-Evidence -Kind 'RunCompleted' -Data ([ordered]@{
        Status = $status
        MeasuredFailures = $MeasuredFailureCount
    })
}
Write-Output ($summary | ConvertTo-Json -Compress)

if ($CleanupFailure) { throw 'WINBRAT cleanup failed; inspect the sanitized evidence summary.' }
if ($RunFailure) { throw 'WINBRAT stability run failed; inspect the sanitized evidence summary.' }
if ($DataPlaneBlocked) { throw 'Lifecycle passed, but dataplane verification was blocked because fixed probes were not tunnel-scoped.' }
