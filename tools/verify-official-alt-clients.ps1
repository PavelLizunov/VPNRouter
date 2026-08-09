# Offline allowlist verification for the two official Phase B clients. This
# script never downloads, installs or launches either client.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path $PSScriptRoot -Parent
$Artifacts = Join-Path $Root 'artifacts\official-alt-clients'
$AmneziaPath = Join-Path $Artifacts 'amneziawg-amd64-2.0.2.msi'
$HysteriaPath = Join-Path $Artifacts 'hysteria-windows-amd64.exe'
$AmneziaSha256 = '1b7308d0c74685193dee5d30fd30f370b5a2748a7f648869cd16f25286efc784'
$AmneziaSignerThumbprint = '141D90A1BA8F61863FBEDDF7DD1D66C1D1E0B128'
$HysteriaSha256 = 'f1f782532aa20fe72574393a0e3775cfe10f7edb07f9af6b7bca5c85e2afdd6c'

function Assert-Hash {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Expected
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'A pinned official client package is missing.'
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected) { throw 'A pinned official client package hash does not match.' }
}

Assert-Hash -Path $AmneziaPath -Expected $AmneziaSha256
$signature = Get-AuthenticodeSignature -LiteralPath $AmneziaPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $AmneziaSignerThumbprint) {
    throw 'The pinned AmneziaWG Authenticode signature is not valid.'
}

Assert-Hash -Path $HysteriaPath -Expected $HysteriaSha256

@(
    [ordered]@{
        Client = 'AmneziaWG'
        Version = '2.0.2'
        HashVerified = $true
        SignatureVerified = $true
    },
    [ordered]@{
        Client = 'Hysteria'
        Version = '2.12.0'
        HashVerified = $true
        SignatureVerified = $false
    }
) | ConvertTo-Json -Compress
