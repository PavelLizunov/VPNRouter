# Orchestrates fixed-WINBRAT stability checks exclusively through
# tools/brat-verify.ps1. This file must never contain WinRM, process, route,
# UI Automation or screen-capture implementation.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ColdCycles', 'Soak', 'Cleanup')]
    [string]$Mode,

    [ValidatePattern('^[0-9A-Za-z.-]{1,32}$')]
    [string]$Version,

    [ValidateRange(1, 50)]
    [int]$Cycles = 10,

    [ValidateRange(1, 1440)]
    [int]$DurationMinutes = 120,

    [ValidateRange(5, 300)]
    [int]$SampleSeconds = 15,

    [string]$RunSinceUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VerifyScript = Join-Path $PSScriptRoot 'brat-verify.ps1'
$EvidenceRoot = Join-Path $Root 'artifacts\brat-stability'
$RunStartedUtc = [DateTimeOffset]::UtcNow
$RunId = $RunStartedUtc.ToString('yyyyMMdd-HHmmss')
$EvidencePath = Join-Path $EvidenceRoot "$RunId-$Mode.jsonl"
$RunFailure = $null
$CleanupFailure = $null
$LifecycleFailure = $null
$DataPlaneFailed = $false
$Mutex = New-Object System.Threading.Mutex($false, 'Local\VPNRouterBratStability')
$MutexHeld = $false

if ($Mode -ne 'Cleanup' -and [string]::IsNullOrWhiteSpace($Version)) {
    throw '-Version is required for ColdCycles and Soak evidence.'
}
if (-not (Test-Path $VerifyScript)) { throw 'tools/brat-verify.ps1 is missing.' }
$RunSince = $null
if ($RunSinceUtc) {
    $parsedSince = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
        $RunSinceUtc,
        'o',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsedSince)) {
        throw '-RunSinceUtc must use round-trip ISO-8601 format.'
    }
    $RunSince = $parsedSince
}

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
        [string]$State.TunState -eq 'Absent'
}

function Test-ConnectedState {
    param($State)
    return [int]$State.GuiCount -eq 1 -and
        [int]$State.CoreCount -eq 1 -and
        [string]$State.TunState -eq 'Up' -and
        [string]$State.RouteScope -eq 'Tunnel'
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
    # Keep Windows PowerShell 5.1 source ASCII-safe while matching EN and RU.
    $codePoints = if ($Target -eq 'Connect') {
        @(0x041F, 0x043E, 0x0434, 0x043A, 0x043B, 0x044E, 0x0447, 0x0438, 0x0442, 0x044C)
    }
    else {
        @(0x041E, 0x0442, 0x043A, 0x043B, 0x044E, 0x0447, 0x0438, 0x0442, 0x044C)
    }
    $localized = -join @($codePoints | ForEach-Object { [char]$_ })
    return "$Target||$localized"
}

function Invoke-Cta {
    param([Parameter(Mandatory = $true)] [ValidateSet('Connect', 'Disconnect')] [string]$Target)
    Invoke-BratVerify -Arguments @{
        Action         = 'uia'
        Name           = Get-CtaNames $Target
        ControlType    = 'Button'
        UiaOperation   = 'Invoke'
        TimeoutSeconds = 10
    } | Out-Null
}

function Assert-Cta {
    param([Parameter(Mandatory = $true)] [ValidateSet('Connect', 'Disconnect')] [string]$Target)
    Invoke-BratVerify -Arguments @{
        Action         = 'uia'
        Name           = Get-CtaNames $Target
        ControlType    = 'Button'
        UiaOperation   = 'Inspect'
        TimeoutSeconds = 10
    } | Out-Null
}

function Ensure-Disconnected {
    try {
        $state = Get-BratState
        if ([int]$state.GuiCount -ne 1) { throw 'Normal cleanup requires exactly one owned VPNRouter GUI.' }
        if ([int]$state.CoreCount -eq 0 -and [string]$state.TunState -eq 'Up') {
            throw 'Normal cleanup found an Up TUN without an owned core.'
        }
        if ([int]$state.CoreCount -gt 0) { Invoke-Cta -Target Disconnect }
        $clean = Wait-BratState -Expected Clean -TimeoutSeconds 30
        Assert-Cta -Target Connect
        Write-Evidence -Kind 'CleanState' -Data $clean
        return $clean
    }
    catch {
        # A crashed GUI cannot service the Disconnect button. Fall back to an
        # identity-verified remote action that stops only exact canonical
        # VPNRouter executable paths and verifies the owned TUN disappeared.
        $emergency = Invoke-BratVerifyJson -Arguments @{ Action = 'emergencycleanup' }
        if ([int]$emergency.CoreCount -ne 0 -or -not [bool]$emergency.TunAbsent) {
            throw 'Emergency cleanup did not prove core-absent/TUN-absent state.'
        }
        $clean = Get-BratState
        if ([int]$clean.CoreCount -ne 0 -or [string]$clean.TunState -ne 'Absent') {
            throw 'Emergency cleanup verification disagrees with remote state.'
        }
        Write-Evidence -Kind 'EmergencyCleanState' -Data $clean
        throw 'Normal UI cleanup failed; emergency recovery reached a safe network state.'
    }
}

function Connect-And-Wait {
    $before = Get-BratState
    if (-not (Test-CleanState $before)) { throw 'Connect requires a clean starting state.' }
    Write-Evidence -Kind 'BeforeConnect' -Data $before
    Invoke-Cta -Target Connect
    $connected = Wait-BratState -Expected Connected -TimeoutSeconds 120
    if ([string]$connected.RouteScope -ne 'Tunnel') { throw 'Connected state is not tunnel-scoped.' }
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

function Get-LifecycleEventCount {
    param(
        [Parameter(Mandatory = $true)] $Lifecycle,
        [Parameter(Mandatory = $true)] [string]$Kind
    )
    $property = $Lifecycle.EventCounts.PSObject.Properties[$Kind]
    if ($null -eq $property) { return 0 }
    return [int]$property.Value
}

function Assert-CleanLifecycle {
    param(
        [Parameter(Mandatory = $true)] $Lifecycle,
        [Parameter(Mandatory = $true)] [string]$Context
    )
    if ([int]$Lifecycle.ErrorCount -gt 0 -or
        [int]$Lifecycle.FatalCount -gt 0 -or
        [int]$Lifecycle.UnknownErrorCount -gt 0) {
        throw "$Context produced error-level lifecycle events."
    }

    foreach ($kind in @(
        'HealthFailed',
        'CoreWedged',
        'RestartRequested',
        'RestartSucceeded',
        'FailoverRequested',
        'FailoverCommitted')) {
        if ((Get-LifecycleEventCount -Lifecycle $Lifecycle -Kind $kind) -gt 0) {
            throw "$Context produced unexpected lifecycle event '$kind'."
        }
    }
}

function Invoke-ColdCycles {
    Ensure-Disconnected | Out-Null
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $cycleStarted = [DateTimeOffset]::UtcNow
        Write-Evidence -Kind 'CycleStarted' -Data ([ordered]@{ Cycle = $cycle })
        Connect-And-Wait | Out-Null

        $probe = Invoke-Probe -Profile Boundary
        if (-not [bool]$probe.Success) {
            $script:DataPlaneFailed = $true
            Write-Evidence -Kind 'DataPlaneProbeFailed' -Data ([ordered]@{ Cycle = $cycle })
        }

        Start-Sleep -Seconds 30
        $held = Get-BratState
        if (-not (Test-ConnectedState $held)) { throw "Cold cycle $cycle disconnected during the hold window." }
        Write-Evidence -Kind 'HeldState' -Data $held

        Ensure-Disconnected | Out-Null
        $lifecycle = Get-Lifecycle -Since $cycleStarted
        Assert-CleanLifecycle -Lifecycle $lifecycle -Context "Cold cycle $cycle"
        Write-Evidence -Kind 'CycleLifecyclePassed' -Data ([ordered]@{ Cycle = $cycle })
    }
    if ($DataPlaneFailed) { throw 'One or more cold-cycle dataplane probes failed.' }
}

function Invoke-Soak {
    Ensure-Disconnected | Out-Null
    $soakStarted = [DateTimeOffset]::UtcNow
    Connect-And-Wait | Out-Null
    $boundary = Invoke-Probe -Profile Boundary
    if (-not [bool]$boundary.Success) { throw 'Initial boundary probe failed.' }

    $deadline = $soakStarted.AddMinutes($DurationMinutes)
    $consecutiveHttpOnly = 0
    $sample = 0
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $sample++
        $state = Get-BratState
        if (-not (Test-ConnectedState $state)) { throw "Soak disconnected unexpectedly at sample $sample." }

        $control = Invoke-Probe -Profile Control
        $httpOk = [bool]$control.Http.Success
        $udpOk = [bool]$control.Udp[0].Success
        $pair = if ($httpOk -and $udpOk) { 'HH' }
            elseif ($httpOk) { 'HNotU' }
            elseif ($udpOk) { 'NotHU' }
            else { 'NotHNotU' }
        Write-Evidence -Kind 'SoakSample' -Data ([ordered]@{ Sample = $sample; Pair = $pair; State = $state })
        if ($pair -eq 'HNotU') { $consecutiveHttpOnly++ } else { $consecutiveHttpOnly = 0 }
        if ($consecutiveHttpOnly -eq 3) {
            Write-Evidence -Kind 'UdpDivergenceIncident' -Data ([ordered]@{ Sample = $sample; Consecutive = 3 })
        }
        Start-Sleep -Seconds $SampleSeconds
    }

    $boundary = Invoke-Probe -Profile Boundary
    if (-not [bool]$boundary.Success) { throw 'Final boundary probe failed.' }
    Ensure-Disconnected | Out-Null
    $lifecycle = Get-Lifecycle -Since $soakStarted
    Assert-CleanLifecycle -Lifecycle $lifecycle -Context 'Soak'
}

try {
    $MutexHeld = $Mutex.WaitOne(0)
    if (-not $MutexHeld) { throw 'Another WINBRAT stability run is active.' }
    Write-Evidence -Kind 'RunStarted' -Data ([ordered]@{
        Cycles = $Cycles; DurationMinutes = $DurationMinutes; SampleSeconds = $SampleSeconds
    })

    switch ($Mode) {
        'ColdCycles' { Invoke-ColdCycles }
        'Soak'       { Invoke-Soak }
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
    if ($MutexHeld -and $RunSince) {
        try {
            $runLifecycle = Get-Lifecycle -Since $RunSince
            Assert-CleanLifecycle -Lifecycle $runLifecycle -Context 'Whole verification run'
            Write-Evidence -Kind 'RunLifecyclePassed' -Data ([ordered]@{ SinceUtc = $RunSince.ToString('o') })
        }
        catch {
            $LifecycleFailure = $_
            Write-Evidence -Kind 'RunLifecycleFailed' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
        }
    }
    if ($MutexHeld) { [void]$Mutex.ReleaseMutex() }
    $Mutex.Dispose()
}

$status = if ($RunFailure -or $CleanupFailure -or $LifecycleFailure) { 'FAIL' } else { 'PASS' }
$summary = [ordered]@{
    Status = $status
    Mode = $Mode
    Evidence = $EvidencePath.Substring($Root.Length).TrimStart('\')
}
Write-Output ($summary | ConvertTo-Json -Compress)

if ($CleanupFailure) { throw 'WINBRAT cleanup failed; inspect the sanitized evidence summary.' }
if ($LifecycleFailure) { throw 'WINBRAT whole-run lifecycle verification failed; inspect the sanitized evidence summary.' }
if ($RunFailure) { throw 'WINBRAT stability run failed; inspect the sanitized evidence summary.' }
