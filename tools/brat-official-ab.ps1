# Coordinates the fixed official-AmneziaWG bracket only through brat-verify.ps1.
# It never accepts or reads a client package, fixture, endpoint or remote target.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Preflight', 'Install', 'Run3', 'Cleanup')]
    [string]$Mode,

    [ValidateSet('Control', 'Target')]
    [string]$Profile = 'Target'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VerifyScript = Join-Path $PSScriptRoot 'brat-verify.ps1'
$EvidenceRoot = Join-Path $Root 'artifacts\brat-stability\official-ab'
$RunId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$EvidenceProfile = if ($Mode -in @('Preflight', 'Run3')) { $Profile } else { 'None' }
$EvidencePath = Join-Path $EvidenceRoot "$RunId-$Mode-$EvidenceProfile.jsonl"
$Mutex = [System.Threading.Mutex]::new($false, 'Local\VPNRouterBratStability')
$MutexHeld = $false
$TerminalStatus = 'PASS'
$NetworkFailures = 0
$RunFailure = $null
$CleanupFailure = $null

if (-not (Test-Path -LiteralPath $VerifyScript -PathType Leaf)) {
    throw 'tools/brat-verify.ps1 is missing.'
}

function Write-OfficialEvidence {
    param(
        [Parameter(Mandatory = $true)] [string]$Kind,
        [Parameter(Mandatory = $false)] $Data
    )

    if (-not (Test-Path -LiteralPath $EvidenceRoot)) {
        New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
    }
    [ordered]@{
        AtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        RunId = $RunId
        Mode = $Mode
        Kind = $Kind
        Data = $Data
    } | ConvertTo-Json -Depth 5 -Compress |
        Add-Content -LiteralPath $EvidencePath -Encoding UTF8
}

function ConvertTo-SafeResult {
    param(
        [Parameter(Mandatory = $true)] $Result,
        [Parameter(Mandatory = $true)] [string]$Operation,
        [int]$Cycle = 0,
        [ValidateSet('None', 'Control', 'Target')]
        [string]$RunProfile = 'None'
    )

    $status = [string]$Result.Status
    if ([string]$Result.Client -ne 'AmneziaWG' -or [string]$Result.Operation -ne $Operation) {
        throw 'Official-client verifier returned a mismatched result identity.'
    }
    if ($Operation -in @('Preflight', 'Cycle') -and [string]$Result.Profile -ne $RunProfile) {
        throw 'Official-client verifier returned a mismatched profile identity.'
    }
    if ($status -notin @('PASS', 'FAIL', 'BLOCKED', 'ABORTED')) {
        throw 'Official-client verifier returned an invalid status.'
    }
    if ($status -eq 'FAIL' -and $Operation -ne 'Cycle') {
        throw 'Only a measured cycle may report a network failure.'
    }

    $lifecycle = [string]$Result.Lifecycle
    if ($lifecycle -notin @(
        'Ready', 'InstallerNotApproved', 'ClientNotInstalled', 'ClientBinaryInvalid',
        'FixtureMissing', 'FixtureAttestationMissing', 'FixtureAclUnsafe', 'VpnRouterNotClean', 'ManagementRouteUnsafe',
        'DirtyClientState', 'InstallFailed', 'Installed', 'EndpointUnavailable',
        'RouteNotTunnel', 'TunnelStateUnavailable', 'PayloadNotApproved',
        'PayloadHashMismatch', 'PayloadMissing', 'PayloadTimeout', 'PayloadExitNonZero',
        'PayloadOutputMissing', 'PayloadOutputEmpty', 'PayloadResultInvalid', 'Completed',
        'ReplyGap', 'CookieFailure', 'NetworkFailure', 'PayloadIntegrityFailure', 'InternalFailure', 'WatchdogFired',
        'TransportLost', 'CleanupFailed', 'Cleaned'
    )) {
        throw 'Official-client verifier returned an invalid lifecycle token.'
    }
    $successLifecycle = @{
        Preflight = 'Ready'
        Install = 'Installed'
        Cycle = 'Completed'
        Cleanup = 'Cleaned'
    }
    if ($status -eq 'PASS' -and $lifecycle -ne $successLifecycle[$Operation]) {
        throw 'Official-client verifier returned a mismatched success lifecycle.'
    }
    if ($status -eq 'FAIL' -and $lifecycle -notin @('ReplyGap', 'CookieFailure', 'NetworkFailure')) {
        throw 'Official-client verifier returned a non-network failure as measured failure.'
    }
    if ($status -ne 'FAIL' -and $lifecycle -in @('ReplyGap', 'CookieFailure', 'NetworkFailure')) {
        throw 'Official-client verifier returned a network lifecycle without measured failure.'
    }
    if (($lifecycle -eq 'PayloadIntegrityFailure') -ne ($status -eq 'ABORTED')) {
        throw 'Official-client verifier returned an integrity lifecycle without fail-closed status.'
    }
    if ($Operation -eq 'Cleanup' -and $status -notin @('PASS', 'ABORTED')) {
        throw 'Official-client verifier returned an invalid cleanup status.'
    }

    $safeMetrics = [ordered]@{}
    $cycleProof = [ordered]@{}
    if ($Operation -eq 'Cycle') {
        $started = [DateTimeOffset]::MinValue
        $ended = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact(
            [string]$Result.StartedUtc, 'o', [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$started
        ) -or -not [DateTimeOffset]::TryParseExact(
            [string]$Result.EndedUtc, 'o', [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$ended
        ) -or $started.Offset -ne [TimeSpan]::Zero -or $ended.Offset -ne [TimeSpan]::Zero -or $ended -lt $started) {
            throw 'Official-client verifier returned invalid cycle chronology.'
        }
        $cycleProof['StartedUtc'] = $started.ToString('o')
        $cycleProof['EndedUtc'] = $ended.ToString('o')
        foreach ($name in @(
            'ManagementRouteIntact', 'ExpectedAdapterRoute', 'AdapterByteCorrelation', 'CleanTeardown'
        )) {
            $property = $Result.PSObject.Properties[$name]
            if ($null -eq $property -or $property.Value -isnot [bool]) {
                throw 'Official-client verifier returned an invalid cycle proof.'
            }
            $cycleProof[$name] = [bool]$property.Value
        }
        if ($status -in @('PASS', 'FAIL') -and @($cycleProof.Values | Where-Object { $_ -is [bool] -and -not $_ }).Count) {
            throw 'Official-client verifier returned measured evidence without complete cycle proof.'
        }
        $requireMetrics = $status -in @('PASS', 'FAIL')
        $metricsProperty = $Result.PSObject.Properties['Metrics']
        if ($requireMetrics -and ($null -eq $metricsProperty -or $null -eq $metricsProperty.Value)) {
            throw 'Official-client verifier omitted the fixed aggregate schema.'
        }
        if ($metricsProperty -and $null -ne $metricsProperty.Value) {
            foreach ($name in @('Sent', 'Received', 'Loss', 'Duplicate', 'Reorder', 'Corruption', 'Unknown')) {
                $property = $metricsProperty.Value.PSObject.Properties[$name]
                if ($null -eq $property) {
                    if ($requireMetrics) { throw 'Official-client verifier omitted the fixed aggregate schema.' }
                    continue
                }
                if ($property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
                    throw 'Official-client verifier returned an invalid aggregate metric.'
                }
                try { $value = [uint64]$property.Value }
                catch { throw 'Official-client verifier returned an invalid aggregate metric.' }
                if ([decimal]$value -ne [decimal]$property.Value) {
                    throw 'Official-client verifier returned an invalid aggregate metric.'
                }
                $safeMetrics[$name] = $value
            }
            foreach ($name in @('RttP50Ms', 'RttP95Ms', 'RttP99Ms', 'MaxAcknowledgedGapMs')) {
                $property = $metricsProperty.Value.PSObject.Properties[$name]
                if ($null -eq $property) {
                    if ($requireMetrics) { throw 'Official-client verifier omitted the fixed aggregate schema.' }
                    continue
                }
                if ($property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
                    throw 'Official-client verifier returned an invalid aggregate metric.'
                }
                try { $value = [double]$property.Value }
                catch { throw 'Official-client verifier returned an invalid aggregate metric.' }
                if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) {
                    throw 'Official-client verifier returned an invalid aggregate metric.'
                }
                $safeMetrics[$name] = $value
            }
        }
        if ($requireMetrics -and
            ($safeMetrics.Received -gt $safeMetrics.Sent -or
             $safeMetrics.Loss -ne ($safeMetrics.Sent - $safeMetrics.Received) -or
             $safeMetrics.RttP50Ms -gt $safeMetrics.RttP95Ms -or
             $safeMetrics.RttP95Ms -gt $safeMetrics.RttP99Ms)) {
            throw 'Official-client verifier returned aggregate metrics that violate fixed invariants.'
        }
    }

    return [ordered]@{
        Operation = $Operation
        Cycle = $Cycle
        Profile = $RunProfile
        Status = $status
        Lifecycle = $lifecycle
        Proof = $cycleProof
        Metrics = $safeMetrics
    }
}

function Set-TerminalStatus {
    param([Parameter(Mandatory = $true)] [string]$Status)

    if ($Status -eq 'ABORTED') { $script:TerminalStatus = 'ABORTED'; return }
    if ($script:TerminalStatus -eq 'ABORTED') { return }
    if ($Status -eq 'BLOCKED') { $script:TerminalStatus = 'BLOCKED'; return }
    if ($script:TerminalStatus -eq 'BLOCKED') { return }
    if ($Status -eq 'FAIL') { $script:TerminalStatus = 'FAIL' }
}

function Invoke-OfficialOperation {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preflight', 'Install', 'Cycle', 'Cleanup')]
        [string]$Operation,
        [int]$Cycle = 0,
        [ValidateSet('None', 'Control', 'Target')]
        [string]$RunProfile = 'None'
    )

    $raw = if ($Operation -in @('Preflight', 'Cycle')) {
        if ($RunProfile -eq 'None') { throw 'Official-client profile is required for this operation.' }
        @(& $VerifyScript -Action altclient -AltClient 'AmneziaWG' -AltOperation $Operation -AltProfile $RunProfile 2>&1)
    }
    else {
        @(& $VerifyScript -Action altclient -AltClient 'AmneziaWG' -AltOperation $Operation 2>&1)
    }
    $json = @($raw | ForEach-Object { $_.ToString() } | Where-Object { $_ -match '^\{' }) |
        Select-Object -Last 1
    if (-not $json) { throw 'Official-client verifier returned no JSON.' }
    try { $result = $json | ConvertFrom-Json }
    catch { throw 'Official-client verifier returned invalid JSON.' }

    $safe = ConvertTo-SafeResult -Result $result -Operation $Operation -Cycle $Cycle -RunProfile $RunProfile
    Write-OfficialEvidence -Kind 'OperationCompleted' -Data $safe
    if ($safe.Status -eq 'FAIL') { $script:NetworkFailures++ }
    Set-TerminalStatus -Status $safe.Status
    return $safe
}

try {
    $MutexHeld = $Mutex.WaitOne(0)
    if (-not $MutexHeld) { throw 'Another official-client bracket is already active.' }
    Write-OfficialEvidence -Kind 'RunStarted' -Data ([ordered]@{
        Client = 'AmneziaWG'
        Profile = $EvidenceProfile
    })

    switch ($Mode) {
        'Preflight' { Invoke-OfficialOperation -Operation Preflight -RunProfile $Profile | Out-Null }
        'Install' { Invoke-OfficialOperation -Operation Install | Out-Null }
        'Run3' {
            $controlPreflight = Invoke-OfficialOperation -Operation Preflight -RunProfile Control
            $continue = $controlPreflight.Status -eq 'PASS'
            if ($continue) {
                $install = Invoke-OfficialOperation -Operation Install
                $continue = $install.Status -eq 'PASS'
            }
            $controlPassed = $continue
            if ($continue) {
                for ($cycle = 1; $cycle -le 3; $cycle++) {
                    $result = Invoke-OfficialOperation -Operation Cycle -Cycle $cycle -RunProfile Control
                    if ($result.Status -ne 'PASS') { $controlPassed = $false }
                    if ($result.Status -in @('BLOCKED', 'ABORTED')) { break }
                }
            }
            if ($Profile -eq 'Target' -and $controlPassed) {
                $targetPreflight = Invoke-OfficialOperation -Operation Preflight -RunProfile Target
                if ($targetPreflight.Status -eq 'PASS') {
                    for ($cycle = 1; $cycle -le 3; $cycle++) {
                        $result = Invoke-OfficialOperation -Operation Cycle -Cycle $cycle -RunProfile Target
                        if ($result.Status -in @('BLOCKED', 'ABORTED')) { break }
                    }
                }
            }
        }
        'Cleanup' { Invoke-OfficialOperation -Operation Cleanup | Out-Null }
    }
}
catch {
    $RunFailure = $_
    $TerminalStatus = 'ABORTED'
    Write-OfficialEvidence -Kind 'RunAborted' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
}
finally {
    if ($MutexHeld -and $Mode -in @('Install', 'Run3')) {
        try {
            $cleanup = Invoke-OfficialOperation -Operation Cleanup
            if ($cleanup.Status -ne 'PASS') {
                throw 'Official-client cleanup did not pass.'
            }
        }
        catch {
            $CleanupFailure = $_
            $TerminalStatus = 'ABORTED'
            Write-OfficialEvidence -Kind 'CleanupAborted' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
        }
    }
    if ($MutexHeld) { [void]$Mutex.ReleaseMutex() }
    $Mutex.Dispose()
}

if (-not $RunFailure -and -not $CleanupFailure) {
    Write-OfficialEvidence -Kind 'RunCompleted' -Data ([ordered]@{
        Status = $TerminalStatus
        NetworkFailures = $NetworkFailures
    })
}

[ordered]@{
    Status = $TerminalStatus
    Mode = $Mode
    RunId = $RunId
    NetworkFailures = $NetworkFailures
    Evidence = $EvidencePath.Substring($Root.Length).TrimStart('\')
} | ConvertTo-Json -Compress

if ($CleanupFailure -or $RunFailure -or $TerminalStatus -eq 'ABORTED') {
    throw 'Official-client bracket aborted.'
}
if ($TerminalStatus -eq 'BLOCKED') {
    throw 'Official-client bracket is blocked.'
}
