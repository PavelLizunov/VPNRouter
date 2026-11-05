#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($env:BusinessConnectionFile) { throw 'Shared Business updates refuse BusinessConnectionFile.' }
foreach ($tool in @('dotnet', 'go', 'git')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Required build tool missing: $tool" }
}
$version = [regex]::Match((Get-Content (Join-Path $root 'VPNRouter.Core\AppVersion.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw 'Source version missing or invalid.' }
$source = (& git -C $root rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $source -notmatch '^[0-9a-f]{40}$') { throw 'Exact source SHA missing.' }
$changes = @(& git -C $root status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Shared update source checkout must be clean.' }
$output = [IO.Path]::GetFullPath($OutputDir)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) {
    throw 'OutputDir must be a new or empty task-owned directory.'
}

$priorEdition = $env:VPNRouterEdition
$priorShared = $env:BusinessSharedUpdate
$priorConnection = $env:BusinessConnectionFile
$priorValidation = $env:VPNROUTER_BUSINESS_SHARED_UPDATE_ZIP
try {
    $env:VPNRouterEdition = 'Business'
    $env:BusinessSharedUpdate = 'true'
    $env:BusinessConnectionFile = $null
    & (Join-Path $root 'build.ps1') -Version $version
    if ($LASTEXITCODE -ne 0) { throw 'Shared Business application build failed.' }

    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $payload = Join-Path $output 'payload'
    Expand-Archive -LiteralPath (Join-Path $root "VPNRouter-v$version-win.zip") -DestinationPath $payload
    $app = Join-Path $payload 'app'
    Get-ChildItem -LiteralPath $app -File |
        Where-Object Name -Match '^VPNRouter\.(GUI|CLI|Service)\.' | Remove-Item -Force
    $geo = Join-Path $app 'geo'
    New-Item -ItemType Directory -Force -Path $geo | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs' -OutFile (Join-Path $geo 'geoip-ru.srs')
    Invoke-WebRequest 'https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-tld-ru.srs' -OutFile (Join-Path $geo 'geosite-ru.srs')
    $receipt = [ordered]@{ schema = 1; edition = 'Business'; architecture = 'win-x64'; version = $version; provisioning = 'external' }
    [IO.File]::WriteAllText((Join-Path $app 'vpnrouter-business-update.json'), ($receipt | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    $zip = Join-Path $output "VPNRouterBusiness-update-v$version-win-x64.zip"
    Compress-Archive -LiteralPath $app -DestinationPath $zip -CompressionLevel Optimal
    $env:VPNROUTER_BUSINESS_SHARED_UPDATE_ZIP = $zip
    & dotnet test (Join-Path $root 'VPNRouter.Tests\VPNRouter.Tests.csproj') -c Release --filter 'FullyQualifiedName~BusinessUpdatePackagingContractTests' --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Shared Business package identity/resource validation failed; do not distribute outputs.' }
    $finalSource = (& git -C $root rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $finalSource -ne $source) { throw 'Source revision changed during the shared build.' }
    & git -C $root diff --quiet HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Tracked source changed during the shared build.' }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
    $source | Set-Content (Join-Path $output 'source-sha.txt') -Encoding ascii
    Write-Host "Credential-free Business update validated at source $source."
} finally {
    $env:VPNRouterEdition = $priorEdition
    $env:BusinessSharedUpdate = $priorShared
    $env:BusinessConnectionFile = $priorConnection
    $env:VPNROUTER_BUSINESS_SHARED_UPDATE_ZIP = $priorValidation
}
