<#
.SYNOPSIS
    Builds VPNRouter distribution ZIP for testers.
.DESCRIPTION
    Publishes GUI, CLI, Service as self-contained win-x64 binaries.
    Bundles sing-box.exe and profiles into a ready-to-use ZIP archive.
.PARAMETER Version
    Version string for the ZIP filename (default: "1.0")
.PARAMETER SingBoxPath
    Path to sing-box.exe to bundle (default: %ProgramData%\VPNRouter\bin\sing-box.exe)
.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version "1.1" -SingBoxPath "C:\tools\sing-box.exe"
#>
param(
    [string]$Version = "1.0",
    [string]$SingBoxPath = "$env:ProgramData\VPNRouter\bin\sing-box.exe"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$PublishDir = Join-Path $Root "publish\dist"
$ZipName = "VPNRouter-v$Version.zip"
$ZipPath = Join-Path $Root $ZipName

Write-Host "=== VPNRouter Build Script ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Output:  $ZipPath"
Write-Host ""

# ── Clean ──
if (Test-Path $PublishDir) {
    Write-Host "[1/6] Cleaning previous build..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $PublishDir
}

# ── Publish GUI (main entry point for testers) ──
Write-Host "[2/6] Publishing VPNRouter.GUI (self-contained)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.GUI\VPNRouter.GUI.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed" }

# ── Publish CLI ──
Write-Host "[3/6] Publishing VPNRouter.CLI (self-contained)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

# ── Publish Service ──
Write-Host "[4/6] Publishing VPNRouter.Service (self-contained)..." -ForegroundColor Yellow
$ServiceDir = Join-Path $PublishDir "service"
New-Item -ItemType Directory -Force -Path $ServiceDir | Out-Null
dotnet publish "$Root\VPNRouter.Service\VPNRouter.Service.csproj" `
    -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $ServiceDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

# ── Clean up unnecessary files ──
Get-ChildItem $PublishDir -Recurse -Include "*.pdb", "appsettings.*.json", "*.runtimeconfig.json" | Remove-Item -Force
Write-Host "       Cleaned PDB/config files" -ForegroundColor Gray

# ── Bundle sing-box.exe ──
Write-Host "[5/6] Bundling sing-box.exe..." -ForegroundColor Yellow
if (Test-Path $SingBoxPath) {
    Copy-Item $SingBoxPath $PublishDir
    Write-Host "       Copied from: $SingBoxPath" -ForegroundColor Gray
} else {
    Write-Host "       WARNING: sing-box.exe not found at $SingBoxPath" -ForegroundColor Red
    Write-Host "       You can add it manually to $PublishDir before zipping" -ForegroundColor Red
}

# ── Bundle profiles ──
$ProfilesSrc = Join-Path $Root "profiles"
$ProfilesDst = Join-Path $PublishDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $ProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $ProfilesDst -Recurse
    Write-Host "       Profiles copied" -ForegroundColor Gray
}

# ── Create README.txt ──
$ReadmePath = Join-Path $PublishDir "README.txt"
@"
VPNRouter v$Version
====================

Quick Start:
1. Run VPNRouter.GUI.exe (accept UAC prompt)
2. Paste your VLESS URI(s) in the Servers tab
3. Select application groups in the Applications tab
4. Click Start VPN

Files:
- VPNRouter.GUI.exe    Main app (tray icon + settings window)
- VPNRouter.CLI.exe    Command-line interface (advanced)
- service\             Windows Service (optional, for auto-start)
- sing-box.exe         VPN engine (auto-copied on first run)
- profiles\            Application profiles

CLI Usage:
  VPNRouter.CLI.exe start --profile Discord_Privacy
  VPNRouter.CLI.exe status
  VPNRouter.CLI.exe stop

Service Installation (run as admin):
  VPNRouter.CLI.exe service install
  VPNRouter.CLI.exe service start
"@ | Set-Content -Path $ReadmePath -Encoding UTF8

# ── Create ZIP ──
Write-Host "[6/6] Creating ZIP archive..." -ForegroundColor Yellow
if (Test-Path $ZipPath) { Remove-Item $ZipPath }
Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal

$zipSize = (Get-Item $ZipPath).Length / 1MB
Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Archive: $ZipPath ($([math]::Round($zipSize, 1)) MB)"
Write-Host ""
Write-Host "Contents:" -ForegroundColor Gray
Get-ChildItem $PublishDir -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace($PublishDir, "").TrimStart("\")
    if ($_.PSIsContainer) { "  $rel\" } else { "  $rel  ($([math]::Round($_.Length/1KB)) KB)" }
}
