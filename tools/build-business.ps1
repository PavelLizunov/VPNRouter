#requires -Version 5.1
# Run only on an authorized build worker or exact-SHA CI. Never upload plaintext outputs.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConnectionFile,
    [Parameter(Mandatory)][string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $ConnectionFile -PathType Leaf)) { throw 'Private connection file missing.' }
$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { throw 'Required Inno Setup SDK missing; do not install it automatically.' }
$version = [regex]::Match((Get-Content (Join-Path $root 'VPNRouter.Core\AppVersion.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Source version missing.' }
$priorEdition = $env:VPNRouterEdition
$priorConnection = $env:BusinessConnectionFile
try {
    $env:VPNRouterEdition = 'Business'
    $env:BusinessConnectionFile = (Resolve-Path -LiteralPath $ConnectionFile).Path
    # Same engine, .NET publishes, runtime payload and hash-verified dependencies.
    & (Join-Path $root 'build.ps1') -Version $version
    if ($LASTEXITCODE -ne 0) { throw 'Business application build failed.' }
    $payload = Join-Path $OutputDir 'payload'
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    Expand-Archive -LiteralPath (Join-Path $root "VPNRouter-v$version-win.zip") -DestinationPath $payload
    # Do not ship alternate consumer entry points with web-repair/update paths.
    Get-ChildItem (Join-Path $payload 'app') -File |
        Where-Object Name -Match '^VPNRouter\.(GUI|CLI|Service)\.' | Remove-Item -Force
    $geo = Join-Path $payload 'app\geo'
    New-Item -ItemType Directory -Force -Path $geo | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs' -OutFile (Join-Path $geo 'geoip-ru.srs')
    Invoke-WebRequest 'https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-tld-ru.srs' -OutFile (Join-Path $geo 'geosite-ru.srs')
    & $iscc "/DAppVersion=$version" "/DPayloadDir=$payload" "/DOutputDir=$OutputDir" /Q (Join-Path $root 'packaging\windows\business.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Business installer compilation failed.' }
    $installer = Join-Path $OutputDir "VPNRouterBusiness-Setup-v$version.exe"
    if (-not (Test-Path $installer)) { throw 'Business installer missing.' }
    $hash = (Get-FileHash -Algorithm SHA256 $installer).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $installer -Leaf)" | Set-Content "$installer.sha256" -Encoding ascii
    Write-Host "Business installer built at source $(& git -C $root rev-parse HEAD). Private distribution only."
} finally {
    $env:VPNRouterEdition = $priorEdition
    $env:BusinessConnectionFile = $priorConnection
}
