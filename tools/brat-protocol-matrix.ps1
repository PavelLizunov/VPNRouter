# Runs the fixed 20-row subscription matrix exclusively through the bounded
# WINBRAT verifier/coordinator. No endpoint, config, route or remote-exec input
# is accepted by this script.
[CmdletBinding()]
param(
    [ValidateRange(2, 3)]
    [int]$Repeats = 3,

    [ValidatePattern('^[0-9]{8}-[0-9]{6}$')]
    [string]$ResumeRunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$StabilityScript = Join-Path $PSScriptRoot 'brat-stability.ps1'
$VerifyScript = Join-Path $PSScriptRoot 'brat-verify.ps1'
$EvidenceRoot = Join-Path $Root 'artifacts\brat-protocol-matrix'
$RunId = if ($ResumeRunId) { $ResumeRunId } else { [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') }
$EvidencePath = Join-Path $EvidenceRoot "$RunId-matrix.jsonl"
$Version = '2.48.0-r8'
$RunFailure = $null
$CleanupFailure = $null
$Mutex = [System.Threading.Mutex]::new($false, 'Local\VPNRouterBratProtocolMatrix')
$MutexHeld = $false

$Manifest = @(
    [ordered]@{ ProtocolClass = 'VlessReality'; Count = 4 },
    [ordered]@{ ProtocolClass = 'VlessWebSocket'; Count = 3 },
    [ordered]@{ ProtocolClass = 'VlessXhttp'; Count = 4 },
    [ordered]@{ ProtocolClass = 'Hysteria2'; Count = 4 },
    [ordered]@{ ProtocolClass = 'AmneziaWG'; Count = 4 },
    [ordered]@{ ProtocolClass = 'Naive'; Count = 1 }
)

if (-not (Test-Path $StabilityScript) -or -not (Test-Path $VerifyScript)) {
    throw 'Required fixed WINBRAT tooling is missing.'
}
if (-not (Test-Path $EvidenceRoot)) {
    New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
}
if ($ResumeRunId -and -not (Test-Path $EvidencePath)) {
    throw 'The requested matrix checkpoint does not exist.'
}

function Write-MatrixEvidence {
    param([Parameter(Mandatory = $true)] [string]$Kind, $Data)
    $record = [ordered]@{
        AtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        RunId = $RunId
        Kind = $Kind
        Data = $Data
    }
    Add-Content -LiteralPath $EvidencePath -Value ($record | ConvertTo-Json -Depth 6 -Compress) -Encoding UTF8
}

function Get-CompletedCells {
    $completed = @{}
    if (-not (Test-Path $EvidencePath)) { return $completed }
    foreach ($line in Get-Content -LiteralPath $EvidencePath) {
        try { $record = $line | ConvertFrom-Json } catch { throw 'Matrix checkpoint contains invalid JSON.' }
        if ([string]$record.Kind -eq 'CellCompleted') {
            $key = '{0}:{1}' -f [string]$record.Data.ProtocolClass, [int]$record.Data.ProtocolOrdinal
            $completed[$key] = $true
        }
    }
    return $completed
}

function Invoke-FixedToolJson {
    param([Parameter(Mandatory = $true)] [string]$Script, [Parameter(Mandatory = $true)] [hashtable]$Arguments)
    $raw = @(& $Script @Arguments 2>&1)
    $line = @($raw | ForEach-Object { $_.ToString() } | Where-Object { $_ -match '^\{"Status"' }) | Select-Object -Last 1
    if (-not $line) { throw 'Fixed WINBRAT tool returned no summary JSON.' }
    try { $summary = $line | ConvertFrom-Json } catch { throw 'Fixed WINBRAT tool returned invalid summary JSON.' }
    if ([string]$summary.Status -eq 'FAIL') {
        throw 'Fixed WINBRAT tool reported an infrastructure or cleanup failure.'
    }
    return $summary
}

function Restore-OriginalUiState {
    Invoke-FixedToolJson -Script $StabilityScript -Arguments @{ Mode = 'Cleanup' } | Out-Null
    try {
        & $VerifyScript -Action uia -Name 'Advanced settings' -ControlType Button -UiaOperation Invoke -TimeoutSeconds 5 | Out-Null
    }
    catch { }
    & $VerifyScript -Action uia -Name 'Subscribe' -ControlType ListItem -UiaOperation Select -TimeoutSeconds 10 | Out-Null
    & $VerifyScript -Action uia -AutomationId SubList -UiaOperation SelectProtocol -ProtocolClass Hysteria2 -ProtocolOrdinal 0 -TimeoutSeconds 25 | Out-Null
    & $VerifyScript -Action uia -Name (([char]0x25C2) + ' Simple') -ControlType Button -UiaOperation Invoke -TimeoutSeconds 10 | Out-Null
    $stateRaw = @(& $VerifyScript -Action state)
    $state = (($stateRaw -join [Environment]::NewLine).Trim() | ConvertFrom-Json)
    if ([int]$state.GuiCount -ne 1 -or [int]$state.CoreCount -ne 0 -or
        [string]$state.TunState -eq 'Up' -or [string]$state.RouteScope -ne 'Direct') {
        throw 'Final WINBRAT state is not clean.'
    }
}

try {
    $MutexHeld = $Mutex.WaitOne(0)
    if (-not $MutexHeld) { throw 'Another protocol matrix is already active.' }
    $completed = Get-CompletedCells
    Write-MatrixEvidence -Kind 'MatrixStarted' -Data ([ordered]@{
        Version = $Version; Repeats = $Repeats; Resume = [bool]$ResumeRunId; TotalRows = 20
    })

    foreach ($family in $Manifest) {
        for ($ordinal = 0; $ordinal -lt [int]$family.Count; $ordinal++) {
            $key = '{0}:{1}' -f [string]$family.ProtocolClass, $ordinal
            if ($completed.ContainsKey($key)) {
                Write-MatrixEvidence -Kind 'CellSkippedCompleted' -Data ([ordered]@{
                    ProtocolClass = [string]$family.ProtocolClass; ProtocolOrdinal = $ordinal
                })
                continue
            }
            Write-MatrixEvidence -Kind 'CellStarted' -Data ([ordered]@{
                ProtocolClass = [string]$family.ProtocolClass; ProtocolOrdinal = $ordinal
            })
            $summary = Invoke-FixedToolJson -Script $StabilityScript -Arguments @{
                Mode = 'ProtocolLoad'; Version = $Version; Cycles = $Repeats
                ProtocolClass = [string]$family.ProtocolClass; ProtocolOrdinal = $ordinal
            }
            Write-MatrixEvidence -Kind 'CellCompleted' -Data ([ordered]@{
                ProtocolClass = [string]$family.ProtocolClass
                ProtocolOrdinal = $ordinal
                Status = [string]$summary.Status
                MeasuredFailures = [int]$summary.MeasuredFailures
                Evidence = [string]$summary.Evidence
            })
        }
    }
    Write-MatrixEvidence -Kind 'MatrixCompleted' -Data ([ordered]@{ TotalRows = 20 })
}
catch {
    $RunFailure = $_
    Write-MatrixEvidence -Kind 'MatrixFailed' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
}
finally {
    if ($MutexHeld) {
        try {
            Restore-OriginalUiState
            Write-MatrixEvidence -Kind 'FinalCleanupPassed' -Data ([ordered]@{ Restored = $true })
        }
        catch {
            $CleanupFailure = $_
            Write-MatrixEvidence -Kind 'FinalCleanupFailed' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
        }
        [void]$Mutex.ReleaseMutex()
    }
    $Mutex.Dispose()
}

$status = if ($RunFailure -or $CleanupFailure) { 'FAIL' } else { 'PASS' }
[ordered]@{
    Status = $status
    RunId = $RunId
    Evidence = $EvidencePath.Substring($Root.Length).TrimStart('\')
} | ConvertTo-Json -Compress
if ($CleanupFailure) { throw 'Protocol matrix final cleanup failed.' }
if ($RunFailure) { throw 'Protocol matrix stopped on an infrastructure failure.' }
