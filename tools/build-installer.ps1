#requires -Version 5.1
<#
.SYNOPSIS
  Builds VPNRouter-Setup-vX.Y.Z.exe (Inno Setup) from the same tree that goes into VPNRouter-vX.Y.Z-win.zip.

.DESCRIPTION
  Takes either the install zip produced by build.ps1 (-PackageZip) or the unpacked package folder (-PackageDir, the one
  that holds Start VPN.cmd, README.txt and app\), compiles packaging\windows\vpnrouter.iss with ISCC.exe, and writes
  VPNRouter-Setup-v<Version>.exe plus a .sha256 sidecar to -OutputDir. The installer is NOT signed and NOT uploaded to
  any release (release wiring is a later, owner-commanded step).

  Inno Setup 6 (ISCC.exe) is looked up in -IsccPath, $env:ISCC, $env:INNO_SETUP, C:\android-build\tools\innosetup and the
  usual Program Files folders. It is not installed by this script.

.EXAMPLE
  .\tools\build-installer.ps1 -Version 2.50.0-r10 -PackageZip .\dist\VPNRouter-v2.50.0-r10-win.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-r[1-9][0-9]*)?$')]
    [string]$Version,

    [string]$PackageZip = '',

    [string]$PackageDir = '',

    [string]$OutputDir = '',

    [string]$IsccPath = ''
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Script = Join-Path $Root 'packaging\windows\vpnrouter.iss'
if (-not (Test-Path $Script)) { throw "Installer script not found: $Script" }

if (($PackageZip -eq '') -eq ($PackageDir -eq '')) {
    throw 'Pass exactly one of -PackageZip or -PackageDir.'
}
if (-not $OutputDir) { $OutputDir = Join-Path $Root 'dist' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

function Find-Iscc {
    param([string]$Explicit)
    $candidates = @($Explicit, $env:ISCC, $env:INNO_SETUP)
    $candidates = $candidates | Where-Object { $_ }
    $resolved = @()
    foreach ($c in $candidates) {
        if (Test-Path $c -PathType Container) { $resolved += (Join-Path $c 'ISCC.exe') } else { $resolved += $c }
    }
    $resolved += 'C:\android-build\tools\innosetup\ISCC.exe'
    $resolved += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    $resolved += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    foreach ($p in $resolved) { if ($p -and (Test-Path $p -PathType Leaf)) { return (Resolve-Path $p).Path } }
    throw 'ISCC.exe (Inno Setup 6) not found. Install it, or pass -IsccPath / set $env:ISCC.'
}

$iscc = Find-Iscc -Explicit $IsccPath

# 1.2.3 or 1.2.3-r4 -> 1.2.3.0 or 1.2.3.4 (the four-part number the file version needs)
$parts = $Version -split '-r'
$numeric = if ($parts.Count -eq 2) { "$($parts[0]).$($parts[1])" } else { "$($parts[0]).0" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("vpnrouter-installer-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    if ($PackageZip) {
        if (-not (Test-Path $PackageZip -PathType Leaf)) { throw "Package zip not found: $PackageZip" }
        $payload = Join-Path $work 'payload'
        Expand-Archive -Path $PackageZip -DestinationPath $payload -Force
    } else {
        if (-not (Test-Path $PackageDir -PathType Container)) { throw "Package folder not found: $PackageDir" }
        $payload = (Resolve-Path $PackageDir).Path
    }

    foreach ($required in @('app\VPNRouter.App.exe', 'app\VPNRouter.CLI.exe')) {
        if (-not (Test-Path (Join-Path $payload $required))) {
            throw "The package does not look like the install zip: $required is missing (expected Start VPN.cmd, README.txt, app\)."
        }
    }

    $iconFile = Join-Path $Root 'VPNRouter.App\Assets\penguin_mascot.ico'
    $isccArgs = @(
        "/DAppVersion=$Version",
        "/DVersionNumeric=$numeric",
        "/DPayloadDir=$payload",
        "/DOutputDir=$OutputDir",
        '/Q'
    )
    if (Test-Path $iconFile) { $isccArgs += "/DIconFile=$iconFile" }
    $isccArgs += $Script

    Write-Host "ISCC: $iscc"
    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$exe = Join-Path $OutputDir "VPNRouter-Setup-v$Version.exe"
if (-not (Test-Path $exe)) { throw "Expected installer not produced: $exe" }

$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
"$hash  $(Split-Path $exe -Leaf)" | Set-Content -Path "$exe.sha256" -Encoding ascii

Write-Host ("Built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
Write-Host "SHA256: $hash"
Write-Host 'Not signed, not uploaded (release wiring is a separate step).'
