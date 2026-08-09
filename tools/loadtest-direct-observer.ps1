# Fixed low-rate observer for the owned load-test endpoint. This intentionally
# accepts no target, protocol or rate input and never launches or changes
# VPNRouter on the dev host.
[CmdletBinding()]
param(
    [ValidateRange(1, 96)]
    [int]$Cycles = 1,

    [ValidatePattern('^\d{8}-\d{6}$')]
    [string]$RunId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$Archive = Join-Path $Root 'artifacts\brat-loadtest-payload\WinbratLoadGen-win-x64.zip'
$ExpectedSha256 = '5855167c4c89efa5c5adbd0933ee4269382785bb35d6b04f7a5fd27d80f72934'
$EvidenceRoot = Join-Path $Root "artifacts\direct-observer\$RunId"
$PayloadRoot = Join-Path $EvidenceRoot 'payload'
$EvidencePath = Join-Path $EvidenceRoot 'observer.jsonl'
$Mutex = [Threading.Mutex]::new($false, 'Local\VPNRouterFixedDirectObserver')
$MutexHeld = $false

function Write-Evidence {
    param(
        [Parameter(Mandatory = $true)] [string]$Kind,
        [Parameter(Mandatory = $true)] [Collections.IDictionary]$Data
    )

    $record = [ordered]@{
        AtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        RunId = $RunId
        Kind = $Kind
        Data = $Data
    }
    $line = $record | ConvertTo-Json -Depth 5 -Compress
    Add-Content -LiteralPath $EvidencePath -Value $line -Encoding utf8
}

try {
    $MutexHeld = $Mutex.WaitOne(0)
    if (-not $MutexHeld) { throw 'The fixed direct observer is already running.' }
    if (-not (Test-Path -LiteralPath $Archive)) { throw 'The approved load payload archive is missing.' }
    $actualHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) { throw 'The approved load payload hash does not match.' }

    New-Item -ItemType Directory -Path $PayloadRoot -Force | Out-Null
    Expand-Archive -LiteralPath $Archive -DestinationPath $PayloadRoot -Force
    $exe = Join-Path $PayloadRoot 'VPNRouter.Tools.WinbratLoadGen.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'The approved load generator executable is missing.' }

    $address = Resolve-DnsName 'loadtest.vpn.ninitux.com' -Type A -ErrorAction Stop | Select-Object -First 1
    $route = Find-NetRoute -RemoteIPAddress $address.IPAddress -ErrorAction Stop | Select-Object -First 1
    $pathScope = if ([string]$route.InterfaceAlias -eq 'VPNRouter-TUN') { 'VPNRouterTunnel' } else { 'ObserverPath' }
    if ($pathScope -ne 'ObserverPath') { throw 'The observer route is not independent of VPNRouter.' }

    Write-Evidence -Kind 'ObserverStarted' -Data ([ordered]@{
        Cycles = $Cycles
        PathScope = $pathScope
        Profile = 'GameUdp'
        Caps = '20pps-256B-burst50pps'
    })

    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $cycleStarted = [DateTimeOffset]::UtcNow
        $route = Find-NetRoute -RemoteIPAddress $address.IPAddress -ErrorAction Stop | Select-Object -First 1
        if ([string]$route.InterfaceAlias -eq 'VPNRouter-TUN') {
            throw 'The observer route changed to VPNRouter during the run.'
        }
        $raw = @(& $exe 2>$null)
        $exitCode = $LASTEXITCODE
        $cycleEnded = [DateTimeOffset]::UtcNow
        $result = $null
        try { $result = ($raw -join [Environment]::NewLine).Trim() | ConvertFrom-Json } catch {}

        $allowed = @('Completed', 'ReplyGap', 'CookieFailure', 'NetworkFailure', 'InternalFailure')
        if ($null -eq $result -or [string]$result.Status -notin $allowed) {
            Write-Evidence -Kind 'ObserverIntegrityFailure' -Data ([ordered]@{ Cycle = $cycle; PathScope = $pathScope })
            throw 'The observer payload returned an invalid aggregate result.'
        }

        Write-Evidence -Kind 'ObserverResult' -Data ([ordered]@{
            Cycle = $cycle
            StartedAtUtc = $cycleStarted.ToString('o')
            EndedAtUtc = $cycleEnded.ToString('o')
            PathScope = $pathScope
            ExitCode = [int]$exitCode
            Status = [string]$result.Status
            Sent = [int]$result.Sent
            Received = [int]$result.Received
            Loss = [int]$result.Loss
            Duplicate = [int]$result.Duplicate
            Reorder = [int]$result.Reorder
            Corruption = [int]$result.Corruption
            Unknown = [int]$result.Unknown
            RttP50Ms = [double]$result.RttP50Ms
            RttP95Ms = [double]$result.RttP95Ms
            RttP99Ms = [double]$result.RttP99Ms
            MaxAcknowledgedGapMs = [double]$result.MaxAcknowledgedGapMs
        })

        if ([string]$result.Status -eq 'InternalFailure') {
            throw 'The observer payload reported an internal failure.'
        }
    }

    Write-Evidence -Kind 'ObserverCompleted' -Data ([ordered]@{ Cycles = $Cycles; PathScope = $pathScope })
}
catch {
    if (Test-Path -LiteralPath $EvidenceRoot) {
        Write-Evidence -Kind 'ObserverFailed' -Data ([ordered]@{ ErrorClass = $_.Exception.GetType().Name })
    }
    throw
}
finally {
    if ($MutexHeld) { $Mutex.ReleaseMutex() }
    $Mutex.Dispose()
}
