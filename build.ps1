<#
.SYNOPSIS
    Builds VPNRouter distribution ZIPs.
.DESCRIPTION
    Publishes GUI, CLI, Service as self-contained win-x64 binaries with SHARED runtime.
    Generates TWO archives:
      - Install ZIP (~48 MB): app/ layout + Start VPN.cmd (for new installs + auto-update)
      - Update ZIP (~3 MB): app binaries only (lite update for existing installs)

    NOTE: Legacy flat ZIP (VPNRouter-v*.zip) was removed in v1.18.0.
    Old clients (v1.17.1 and earlier) will not auto-detect this release.
.PARAMETER Version
    Version string for the ZIP filename (default: "1.0")
.PARAMETER SingBoxPath
    Path to sing-box.exe to bundle (default: %ProgramData%\VPNRouter\bin\sing-box.exe)
.PARAMETER Upload
    Upload the ZIPs to GitHub Releases using gh CLI
.PARAMETER GitHubRepo
    GitHub repo in "owner/repo" format (default: PavelLizunov/VPNRouter)
.EXAMPLE
    .\build.ps1 -Version "1.18.0"
    .\build.ps1 -Version "1.18.0" -Upload
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
$PackageDir = Join-Path $Root "publish\package"
$InstallZipName = "VPNRouter-v$Version-win.zip"
$UpdateZipName = "VPNRouter-update-v$Version-win.zip"
$InstallZipPath = Join-Path $Root $InstallZipName
$UpdateZipPath = Join-Path $Root $UpdateZipName

Write-Host "=== VPNRouter Build Script ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Install: $InstallZipPath"
Write-Host "Update:  $UpdateZipPath"
Write-Host ""

# ── Clean ──
Write-Host "[1/9] Cleaning previous build..." -ForegroundColor Yellow
foreach ($dir in @($DistDir, $FdDir, $UpdateDir, $PackageDir)) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

# ── Publish all three self-contained to SAME dir (shared runtime) ──
Write-Host "[2/9] Publishing VPNRouter.App (Avalonia, self-contained)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.App\VPNRouter.App.csproj" `
    -c Release -r win-x64 --self-contained `
    -o $DistDir 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

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

# ── Build backwards-compat launcher stub (VPNRouter.GUI.exe) ──
# Old auto-updater (v2.3.x) and old shortcuts expect VPNRouter.GUI.exe.
# Native Go exe — ~2MB, zero runtime dependency, runs on machines without .NET 8.
Write-Host "[4b/9] Building VPNRouter.GUI launcher stub (Go native)..." -ForegroundColor Yellow
$stubExe = Join-Path $DistDir "VPNRouter.GUI.exe"
$env:GOOS = "windows"
$env:GOARCH = "amd64"
Push-Location "$Root\VPNRouter.GUI"
go build -ldflags="-s -w -H windowsgui" -o $stubExe .\main.go 2>&1 | Out-Null
$stubExitCode = $LASTEXITCODE
Pop-Location
if ($stubExitCode -ne 0) { throw "GUI stub build failed (is Go installed?)" }

# ── Publish framework-dependent to temp dir (to identify app-only files) ──
Write-Host "[5/9] Building app file list (framework-dependent)..." -ForegroundColor Yellow
dotnet publish "$Root\VPNRouter.App\VPNRouter.App.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
dotnet publish "$Root\VPNRouter.CLI\VPNRouter.CLI.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
dotnet publish "$Root\VPNRouter.Service\VPNRouter.Service.csproj" `
    -c Release -r win-x64 --self-contained false --no-build `
    -o $FdDir 2>&1 | Out-Null
# Also copy stub to FdDir so update zip includes it
Copy-Item $stubExe $FdDir -Force
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

# ── Remove WPF DLLs (~41 MB) — app uses WinForms only, no WPF ──
$wpfPatterns = @(
    "PresentationFramework*.dll", "PresentationCore.dll", "PresentationUI.dll",
    "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll",
    "System.Xaml.dll", "System.Windows.Controls.Ribbon.dll",
    "ReachFramework.dll", "System.Printing.dll",
    "System.Windows.Input.Manipulations.dll", "System.Windows.Presentation.dll",
    "System.IO.Packaging.dll", "DirectWriteForwarder.dll",
    "PenImc_cor3.dll", "vcruntime140_cor3.dll",
    "WindowsBase.dll", "WindowsFormsIntegration.dll"
)
$wpfRemoved = 0
foreach ($pattern in $wpfPatterns) {
    Get-ChildItem $DistDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        $wpfRemoved += $_.Length
        Remove-Item $_.FullName -Force
    }
}

# ── Remove TraceEvent non-essential natives (~9 MB) ──
# App is win-x64: arm64/ and x86/ folders not needed
# msdia140.dll = symbol resolution (not used for ETW monitoring)
# Microsoft.DiaSymReader.Native = symbol reading (not needed)
# Keep only amd64/KernelTraceControl.dll (required for ETW)
$nativeRemoved = 0
foreach ($dir in @("arm64", "x86")) {
    $dirPath = Join-Path $DistDir $dir
    if (Test-Path $dirPath) {
        $nativeRemoved += (Get-ChildItem $dirPath -File -Recurse | Measure-Object Length -Sum).Sum
        Remove-Item $dirPath -Recurse -Force
    }
}
$msdia = Join-Path $DistDir "amd64\msdia140.dll"
if (Test-Path $msdia) {
    $nativeRemoved += (Get-Item $msdia).Length
    Remove-Item $msdia -Force
}
$diasym = Join-Path $DistDir "Microsoft.DiaSymReader.Native.amd64.dll"
if (Test-Path $diasym) {
    $nativeRemoved += (Get-Item $diasym).Length
    Remove-Item $diasym -Force
}

# ── Remove design-time / unused assemblies (~7 MB) ──
$unusedAssemblies = @(
    "System.Windows.Forms.Design.dll", "System.Windows.Forms.Design.Editors.dll",
    "Microsoft.VisualBasic.Core.dll", "System.CodeDom.dll",
    "System.DirectoryServices.dll"
)
$designRemoved = 0
foreach ($pattern in $unusedAssemblies) {
    Get-ChildItem $DistDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        $designRemoved += $_.Length
        Remove-Item $_.FullName -Force
    }
}

$totalSaved = ($wpfRemoved + $nativeRemoved + $designRemoved) / 1MB
Write-Host "       Cleaned PDB, locale, debug, WPF, and unused files" -ForegroundColor Gray
Write-Host "       Removed: WPF $([math]::Round($wpfRemoved/1MB,1)) MB + natives $([math]::Round($nativeRemoved/1MB,1)) MB + design $([math]::Round($designRemoved/1MB,1)) MB = $([math]::Round($totalSaved,1)) MB saved" -ForegroundColor Gray

# ── Bundle sing-box.exe ──
Write-Host "[6/9] Bundling sing-box.exe..." -ForegroundColor Yellow
if (Test-Path $SingBoxPath) {
    Copy-Item $SingBoxPath $DistDir
    Write-Host "       Copied from: $SingBoxPath" -ForegroundColor Gray
} else {
    Write-Host "       WARNING: sing-box.exe not found at $SingBoxPath" -ForegroundColor Red
}

# ── Bundle zapret (DPI bypass, Windows-only) ──
$ZapretSrc = Join-Path $Root "tools\zapret"
$ZapretDst = Join-Path $DistDir "zapret"
if (Test-Path $ZapretSrc) {
    New-Item -ItemType Directory -Force -Path $ZapretDst | Out-Null
    Copy-Item "$ZapretSrc\*" $ZapretDst -Recurse
    Write-Host "       Zapret bundled ($(Get-ChildItem $ZapretDst -File | Measure-Object Length -Sum | ForEach-Object { [math]::Round($_.Sum/1KB) }) KB)" -ForegroundColor Gray
} else {
    Write-Host "       WARNING: zapret not found at $ZapretSrc" -ForegroundColor Yellow
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
1. Double-click "Start VPN.cmd" (or run app\VPNRouter.App.exe directly)
2. Accept the UAC prompt
3. Paste your VLESS URI(s) in the Servers tab
4. Select application groups in the Applications tab
5. Click Start VPN

Folder Structure:
- Start VPN.cmd            Launcher (double-click to start)
- README.txt               This file
- app\                     Application files
  - VPNRouter.App.exe      Main app (Avalonia GUI, tray icon, settings)
  - VPNRouter.CLI.exe      Command-line interface (advanced)
  - VPNRouter.Service.exe  Windows Service (optional, for auto-start)
  - sing-box.exe           VPN engine (auto-copied on first run)
  - profiles\              Application profiles

CLI Usage (run from app\ folder):
  VPNRouter.CLI.exe start --profile Discord_Privacy
  VPNRouter.CLI.exe status
  VPNRouter.CLI.exe stop

Service Installation (run as admin):
  VPNRouter.CLI.exe service install
  VPNRouter.CLI.exe service start
"@ | Set-Content -Path $ReadmePath -Encoding UTF8

# ── Create clean package layout (app/ subfolder + launcher) ──
Write-Host "[7/9] Creating package layout..." -ForegroundColor Yellow
$AppDir = Join-Path $PackageDir "app"
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null

# Copy all dist files into app/
Copy-Item "$DistDir\*" $AppDir -Recurse

# Create Start VPN.cmd launcher in package root
'@start "" "%~dp0app\VPNRouter.App.exe"' | Set-Content (Join-Path $PackageDir "Start VPN.cmd") -Encoding ASCII

# Move README to package root (user-facing, not buried in app/)
Move-Item (Join-Path $AppDir "README.txt") (Join-Path $PackageDir "README.txt") -Force

Write-Host "       Package layout: Start VPN.cmd + README.txt + app/" -ForegroundColor Gray

# ── Create INSTALL ZIP (app/ structure — for new installs + auto-update) ──
Write-Host "[8/9] Creating install ZIP (app/ layout)..." -ForegroundColor Yellow
if (Test-Path $InstallZipPath) { Remove-Item $InstallZipPath }
Compress-Archive -Path "$PackageDir\*" -DestinationPath $InstallZipPath -CompressionLevel Optimal

# ── Create UPDATE ZIP (app files + sing-box + profiles) ──
Write-Host "[9/9] Creating update ZIP..." -ForegroundColor Yellow
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
# Include sing-box.exe (version may change between releases)
$singBoxInDist = Join-Path $DistDir "sing-box.exe"
if (Test-Path $singBoxInDist) {
    Copy-Item $singBoxInDist $UpdateDir
    $updateFileCount++
    Write-Host "       sing-box.exe included in update" -ForegroundColor Gray
}
# Also include profiles and README
$UpdateProfilesDst = Join-Path $UpdateDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $UpdateProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $UpdateProfilesDst -Recurse
}
Copy-Item $ReadmePath $UpdateDir

Write-Host "       Update package: $updateFileCount files" -ForegroundColor Gray

if (Test-Path $UpdateZipPath) { Remove-Item $UpdateZipPath }
Compress-Archive -Path "$UpdateDir\*" -DestinationPath $UpdateZipPath -CompressionLevel Optimal

# ── Clean temp dirs ──
Remove-Item -Recurse -Force $FdDir
Remove-Item -Recurse -Force $UpdateDir
Remove-Item -Recurse -Force $PackageDir

# ── Summary ──
$installSize = (Get-Item $InstallZipPath).Length / 1MB
$updateSize = (Get-Item $UpdateZipPath).Length / 1MB

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "Install ZIP: $InstallZipPath ($([math]::Round($installSize, 1)) MB)" -ForegroundColor White
Write-Host "Update ZIP:  $UpdateZipPath ($([math]::Round($updateSize, 1)) MB)" -ForegroundColor White
Write-Host ""

Write-Host "Package contents:" -ForegroundColor Gray
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

        gh release create $tag $InstallZipPath $UpdateZipPath `
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
