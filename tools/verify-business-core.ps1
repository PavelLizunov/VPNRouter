#requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'packaging\windows\business-core.ps1')
$asset = $BusinessSingBoxAsset
$work = Join-Path ([IO.Path]::GetTempPath()) ("vpnrouter-business-core-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    & gh release download "v$BusinessSingBoxVersion" --repo PavelLizunov/sing-box-vpnctl `
        --pattern $asset --dir $work --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Business core download failed.' }
    $zip = Join-Path $work $asset
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    if ($actual -ne $BusinessSingBoxSha256) {
        throw "Business core SHA256 mismatch: expected $BusinessSingBoxSha256 but got $actual"
    }
    & gh attestation verify $zip --repo PavelLizunov/sing-box-vpnctl
    if ($LASTEXITCODE -ne 0) { throw 'Business core attestation verification failed.' }
    Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
    $core = Join-Path $work "sing-box-$BusinessSingBoxVersion-windows-amd64\sing-box.exe"
    if (-not (Test-Path -LiteralPath $core -PathType Leaf)) {
        throw 'Pinned Business core archive has no sing-box.exe.'
    }
    $version = @(& $core version | Where-Object { $_ -match '^sing-box version ' } |
        Select-Object -First 1)[0]
    if ($LASTEXITCODE -ne 0 -or -not $version -or
        $version.Trim() -cne "sing-box version $BusinessSingBoxVersion") {
        throw "Packaged Business core reported an unexpected version: $version"
    }
    Write-Host "Business core artifact verified: $asset ($BusinessSingBoxSha256), $version"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
