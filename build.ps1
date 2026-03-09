<#
.SYNOPSIS
    Builds VPNRouter distribution ZIPs.
.DESCRIPTION
    Publishes GUI, CLI, Service as self-contained win-x64 binaries with SHARED runtime.
    Generates TWO archives:
      - Full ZIP (~50 MB): runtime + apps + sing-box + profiles (for new installs)
      - Update ZIP (~5-10 MB): app binaries only (for existing installs)
.PARAMETER Version
    Version string for the ZIP filename (default: "1.0")
.PARAMETER SingBoxPath
    Path to sing-box.exe to bundle (default: %ProgramData%\VPNRouter\bin\sing-box.exe)
.PARAMETER Upload
    Upload the ZIPs to GitHub Releases using gh CLI
.PARAMETER GitHubRepo
    GitHub repo in "owner/repo" format (default: PavelLizunov/VPNRouter)
.EXAMPLE
    .\build.ps1 -Version "1.17.0"
    .\build.ps1 -Version "1.17.0" -Upload
#>
param(
    [string]$Version = "1.0",
    [string]$SingBoxPath = "$env:ProgramData\VPNRouter\bin\sing-box.exe",
    [switch]$Upload,
    [string]$GitHubRepo = "PavelLizunov/VPNRouter"
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$DistDir = Join-Path $Root "publish\dist"
$FdDir = Join-Path $Root "publish\fd"
$UpdateDir = Join-Path $Root "publish\update"
$FullZipName = "VPNRouter-v$Version.zip"
$UpdateZipName = "VPNRouter-update-v$Version.zip"
$FullZipPath = Join-Path $Root $FullZipName
$UpdateZipPath = Join-Path $Root $UpdateZipName

Write-Host "=== VPNRouter Build Script ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Full:    $FullZipPath"
Write-Host "Update:  $UpdateZipPath"
Write-Host ""

# ── Clean ──
Write-Host "[1/9] Cleaning previous build..." -ForegroundColor Yellow
foreach ($dir in @($DistDir, $FdDir, $UpdateDir)) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

# ── Publish all three self-contained to SAME dir (shared runtime) ──
Write-Host "[2/9] Publishing VPNRouter.GUI (self-contained, shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.GUI\VPNRouter.GUI.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed" }

Write-Host "[3/9] Publishing VPNRouter.CLI (self-contained, shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

Write-Host "[4/9] Publishing VPNRouter.Service (self-contained, shared runtime)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.Service\VPNRouter.Service.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service publish failed" }

# ── Publish framework-dependent to temp dir (to identify app-only files) ──
Write-Host "[5/9] Building app file list (framework-dependent)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.GUI\VPNRouter.GUI.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
dotnet publish "$Root\VPNRouter.Service\VPNRouter.Service.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
Write-Host "       App files identified: $((Get-ChildItem $FdDir -File).Count) files" -ForegroundColor Gray

# ── Clean unnecessary files from dist ──
Get-ChildItem $DistDir -Recurse -Include "*.pdb", "appsettings.*.json" | Remove-Item -Force

# Remove unused localization satellite assemblies (WPF/WinForms resources for languages we don't use)
# Keeps only 'en' (default, embedded in main DLLs). Saves ~15 MB.
$localeDirs = @("cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "sv", "tr", "zh-Hans", "zh-Hant")
foreach ($locale in $localeDirs) {
    $localeDir = Join-Path $DistDir $locale
    if (Test-Path $localeDir) { Remove-Item -Recurse -Force $localeDir }
}

# Remove debug/diagnostic tools only (conservative — don't remove runtime DLLs)
$unusedFiles = @(
    "createdump.exe",
    "mscordaccore.dll", "mscordaccore_amd64_amd64_*.dll", "mscordbi.dll"
)
foreach ($pattern in $unusedFiles) {
    Get-ChildItem $DistDir -Filter $pattern | Remove-Item -Force -ErrorAction SilentlyContinue
}

Write-Host "       Cleaned PDB, locale, and debug files" -ForegroundColor Gray

# ── Bundle sing-box.exe ──
Write-Host "[6/9] Bundling sing-box.exe..." -ForegroundColor Yellow
if (Test-Path $SingBoxPath) {
    Copy-Item $SingBoxPath $DistDir
    Write-Host "       Copied from: $SingBoxPath" -ForegroundColor Gray
} else {
    Write-Host "       WARNING: sing-box.exe not found at $SingBoxPath" -ForegroundColor Red
}

# ── Bundle profiles ──
$ProfilesSrc = Join-Path $Root "profiles"
$ProfilesDst = Join-Path $DistDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $ProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $ProfilesDst -Recurse
    Write-Host "       Profiles copied" -ForegroundColor Gray
}

# ── Create README.txt ──
$ReadmePath = Join-Path $DistDir "README.txt"
@"
VPNRouter v$Version
====================

Quick Start:
1. Run VPNRouter.GUI.exe (accept UAC prompt)
2. Paste your VLESS URI(s) in the Servers tab
3. Select application groups in the Applications tab
4. Click Start VPN

Files:
- VPNRouter.GUI.exe        Main app (tray icon + settings window)
- VPNRouter.CLI.exe        Command-line interface (advanced)
- VPNRouter.Service.exe    Windows Service (optional, for auto-start)
- sing-box.exe             VPN engine (auto-copied on first run)
- profiles\                Application profiles

CLI Usage:
  VPNRouter.CLI.exe start --profile Discord_Privacy
  VPNRouter.CLI.exe status
  VPNRouter.CLI.exe stop

Service Installation (run as admin):
  VPNRouter.CLI.exe service install
  VPNRouter.CLI.exe service start
"@ | Set-Content -Path $ReadmePath -Encoding UTF8

# ── Create FULL ZIP ──
Write-Host "[7/9] Creating full ZIP..." -ForegroundColor Yellow
if (Test-Path $FullZipPath) { Remove-Item $FullZipPath }
Compress-Archive -Path "$DistDir\*" -DestinationPath $FullZipPath -CompressionLevel Optimal

# ── Create UPDATE ZIP (app files only, no runtime, no sing-box) ──
Write-Host "[8/9] Creating update ZIP..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $UpdateDir | Out-Null

# Copy app-only files from dist (using fd file list as reference)
$fdFileNames = (Get-ChildItem $FdDir -File).Name | Sort-Object -Unique
$updateFileCount = 0
foreach ($name in $fdFileNames) {
    $src = Join-Path $DistDir $name
    if (Test-Path $src) {
        Copy-Item $src $UpdateDir
        $updateFileCount++
    }
}
# Also include profiles and README
$UpdateProfilesDst = Join-Path $UpdateDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $UpdateProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $UpdateProfilesDst -Recurse
}
Copy-Item $ReadmePath $UpdateDir

Write-Host "       Update package: $updateFileCount app files" -ForegroundColor Gray

if (Test-Path $UpdateZipPath) { Remove-Item $UpdateZipPath }
Compress-Archive -Path "$UpdateDir\*" -DestinationPath $UpdateZipPath -CompressionLevel Optimal

# ── Clean temp dirs ──
Remove-Item -Recurse -Force $FdDir
Remove-Item -Recurse -Force $UpdateDir

# ── Summary ──
$fullSize = (Get-Item $FullZipPath).Length / 1MB
$updateSize = (Get-Item $UpdateZipPath).Length / 1MB

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Full ZIP:   $FullZipPath ($([math]::Round($fullSize, 1)) MB)" -ForegroundColor White
Write-Host "Update ZIP: $UpdateZipPath ($([math]::Round($updateSize, 1)) MB)" -ForegroundColor White
Write-Host ""

Write-Host "[9/9] Full package contents:" -ForegroundColor Gray
Get-ChildItem $DistDir -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace($DistDir, "").TrimStart("\")
    if ($_.PSIsContainer) { "  $rel\" } else { "  $rel  ($([math]::Round($_.Length/1KB)) KB)" }
}

# ── Upload to GitHub Releases (optional) ──
if ($Upload) {
    Write-Host ""
    Write-Host "Uploading to GitHub Releases..." -ForegroundColor Yellow

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "       ERROR: gh CLI not found. Install: winget install GitHub.cli" -ForegroundColor Red
    } else {
        $tag = "v$Version"

        gh release create $tag $FullZipPath $UpdateZipPath `
            --repo $GitHubRepo `
            --title "VPNRouter v$Version" `
            --notes "VPNRouter v$Version" `
            --latest

        if ($LASTEXITCODE -eq 0) {
            Write-Host "       Uploaded: https://github.com/$GitHubRepo/releases/tag/$tag" -ForegroundColor Green
        } else {
            Write-Host "       Upload failed (exit $LASTEXITCODE)" -ForegroundColor Red
        }
    }
}
