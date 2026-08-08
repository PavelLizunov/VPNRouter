# Fixed-profile WINBRAT load-test coordinator. Remote work is delegated only to
# brat-verify.ps1; this script never changes VPNRouter settings or selected apps.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('GameUdp', 'BrowserBurst', 'Mixed')]
    [string]$Profile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$VerifyScript = Join-Path $PSScriptRoot 'brat-verify.ps1'
$EvidenceRoot = Join-Path $Root 'artifacts\brat-loadtest'
if (-not (Test-Path $VerifyScript)) { throw 'tools/brat-verify.ps1 is missing.' }
if (-not (Test-Path $EvidenceRoot)) { New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null }

$raw = @(& $VerifyScript -Action loadtest -LoadProfile $Profile)
$result = ($raw -join [Environment]::NewLine).Trim() | ConvertFrom-Json
if ([string]$result.Status -ne 'BLOCKED' -and [string]$result.Status -ne 'PASS') { throw 'Verifier returned an invalid load-test status.' }
if ([string]$result.Lifecycle -notin @('PayloadNotApproved', 'EndpointUnavailable', 'MeasurementGated', 'Completed')) { throw 'Verifier returned an invalid lifecycle enum.' }

# Evidence is an allowlist. Do not add target, route, token, process, config or
# remoting fields here; the verifier already reduced route state to an enum.
$evidence = [ordered]@{
    Profile = [string]$result.Profile
    Caps = [string]$result.Caps
    DurationSeconds = [int]$result.DurationSeconds
    Status = [string]$result.Status
    RouteScope = [string]$result.RouteScope
    Metrics = [ordered]@{}
    Lifecycle = [string]$result.Lifecycle
}
$path = Join-Path $EvidenceRoot ("{0}-{1}.json" -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'), $Profile)
$evidence | ConvertTo-Json -Depth 4 -Compress | Set-Content -LiteralPath $path -Encoding utf8
Write-Output ($evidence | ConvertTo-Json -Depth 4 -Compress)
if ($result.Status -eq 'BLOCKED') { throw 'Load test blocked: operator endpoint or tunnel proof is unavailable.' }
