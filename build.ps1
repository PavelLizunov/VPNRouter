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

    When -AndroidAlso is supplied the script also attempts a local APK build
    after the Windows artifacts. APK builds normally happen on CI
    (build-android.yml, see plans/vpnrouter-android-platform-parity-roadmap.md
    Phase A); -AndroidAlso is a contributor convenience for sanity-checking
    Android-side changes before pushing a tag.
.PARAMETER Version
    Version string for the ZIP filename (default: "1.0")
.PARAMETER SingBoxPath
    Path to sing-box.exe to bundle (default: %ProgramData%\VPNRouter\bin\sing-box.exe)
.PARAMETER Upload
    Upload the ZIPs to GitHub Releases using gh CLI
.PARAMETER GitHubRepo
    GitHub repo in "owner/repo" format (default: PavelLizunov/VPNRouter)
.PARAMETER AndroidAlso
    Also build a signed Android APK (arm64) after the Windows artifacts.
    Requires JAVA_HOME (JDK 17), ANDROID_HOME (Android SDK), the dotnet
    'android' workload, and a signing keystore. The keystore is resolved
    from env vars ANDROID_KEYSTORE_PATH + ANDROID_KEYSTORE_PASSWORD
    (optionally ANDROID_KEYSTORE_KEY_ALIAS / ANDROID_KEYSTORE_KEY_PASSWORD),
    or from a `.env.local` file at the repo root with the same keys, or
    from a `vpnrouter.keystore` file inside VPNRouter.Android\. If any
    prerequisite is missing the Android build is skipped with a warning;
    the Windows artifacts above remain valid. When combined with -Upload
    the APK + .sha256 are added to the release assets.
.EXAMPLE
    .\build.ps1 -Version "1.18.0"
    .\build.ps1 -Version "1.18.0" -Upload
    .\build.ps1 -Version "2.32.0-r1" -AndroidAlso
    .\build.ps1 -Version "2.32.0" -Upload -AndroidAlso
#>
param(
    [string]$Version = "1.0",
    # SingBoxVersion: upstream sing-box release to bundle (v2.27.2+).
    # Keep aligned with Linux workflow (.github/workflows/build-linux.yml)
    # and build-mac.sh — all three platforms ship the same sing-box release.
    [string]$SingBoxVersion = "1.13.10",
    # Optional override: pre-existing sing-box.exe to bundle instead of
    # downloading upstream. Used for local testing of custom builds.
    # Empty string means "auto-download upstream $SingBoxVersion".
    [string]$SingBoxPath = "",
    [switch]$Upload,
    [string]$GitHubRepo = "PavelLizunov/VPNRouter",
    # Build a local Android APK alongside the Windows artifacts. See
    # PARAMETER AndroidAlso for prereqs. Falls back to a clear warning
    # (not a hard failure) when prereqs are missing.
    [switch]$AndroidAlso
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

# ── v2.29.0-r7 LAYER 1: AppVersion match check ──
# Trigger: v2.29.0-r1..r5 dev cycle bug (CLAUDE-AI fake-tag fiasco).
# build.ps1 was being run from main repo's working directory while the
# author was developing in a worktree. main repo's AppVersion.cs was
# stuck at v2.28.7 while the -Version arg said "2.29.0-r5". Build
# silently produced v2.28.7 binary tagged as v2.29.0-r5. Users on
# Windows clicked Update and got the same v2.28.7 binary back.
#
# Fix: compare -Version CLI arg with AppVersion.cs literal at build
# start. Mismatch -> abort with a clear remediation hint. Catches the
# entire class of "compiled wrong source tree" bugs in 0 seconds.
#
# See plans/vpnrouter-update-reliability-strategy.md Layer 1.
$appVersionFile = Join-Path $Root "VPNRouter.Core\AppVersion.cs"
if (-not (Test-Path $appVersionFile)) {
    throw "ABORT: AppVersion.cs not found at $appVersionFile. Are you running build.ps1 from the wrong directory?"
}
$appVersionLine = (Get-Content $appVersionFile |
    Select-String 'public const string Version =' | Select-Object -First 1).Line
if (-not $appVersionLine) {
    throw "ABORT: could not parse 'public const string Version =' from $appVersionFile."
}
# Extract the string between the first pair of double quotes.
if ($appVersionLine -match '"([^"]+)"') {
    $srcVersion = $Matches[1]
} else {
    throw "ABORT: AppVersion.cs Version line has no quoted value: $appVersionLine"
}
if ($srcVersion -ne $Version) {
    throw @"
ABORT: -Version '$Version' does not match AppVersion.cs '$srcVersion'.

This is the v2.29.0-r1..r5 fake-tag bug. Either:
  (a) Bump $appVersionFile Version constant to '$Version' and commit, OR
  (b) Run build.ps1 with -Version '$srcVersion' to match the source on disk, OR
  (c) If you're working in a worktree, make sure you've pulled main repo:
        cd '$Root' ; git pull --ff-only origin main
      then re-run.

Refusing to ship a binary whose AppVersion does not match the release tag.
"@
}
Write-Host "[0/9] AppVersion match: $srcVersion = -Version $Version OK" -ForegroundColor Green

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
#
# v2.31.9-r1 trampoline: stub now ALSO performs integrity check (PE
# version-info read of App/Core/Service.dll) before launching App.exe,
# and self-repairs on mixed-version damage by spawning install.ps1.
# `ChannelHint` ldflag carries the build's channel forward so a repair
# triggered from a -rN binary lands back on the prerelease channel.
# `main.go` only — package contains integrity.go, marker.go, repair.go,
# integrity_test.go alongside but the build target is the whole pkg.
Write-Host "[4b/9] Building VPNRouter.GUI launcher stub (Go native)..." -ForegroundColor Yellow
$stubExe = Join-Path $DistDir "VPNRouter.GUI.exe"
$env:GOOS = "windows"
$env:GOARCH = "amd64"
# Channel inferred from -Version: anything with "-r" suffix → prerelease.
if ($Version -match '-r\d+$') { $stubChannel = "prerelease" } else { $stubChannel = "stable" }
$stubLdflags = "-s -w -H windowsgui -X main.ChannelHint=$stubChannel"
Push-Location "$Root\VPNRouter.GUI"
go build -ldflags="$stubLdflags" -o $stubExe . 2>&1 | Out-Null
$stubExitCode = $LASTEXITCODE
Pop-Location
if ($stubExitCode -ne 0) { throw "GUI stub build failed (is Go installed?)" }
Write-Host "       Stub channel: $stubChannel" -ForegroundColor Gray

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
# v2.27.2: auto-download upstream sing-box prebuild by default. Previously
# required -SingBoxPath pointing at a pre-installed copy (%ProgramData%\VPNRouter\bin\)
# which in turn was populated via build-singbox.ps1 (custom Go rebuild).
# Now falls back to upstream download, keeping manual rebuild as an opt-in
# override via -SingBoxPath. See build-singbox.ps1 header for rationale.
Write-Host "[6/9] Bundling sing-box.exe..." -ForegroundColor Yellow
if ($SingBoxPath -and (Test-Path $SingBoxPath)) {
    Copy-Item $SingBoxPath (Join-Path $DistDir "sing-box.exe") -Force
    Write-Host "       Copied from: $SingBoxPath (override)" -ForegroundColor Gray
} else {
    # Auto-download upstream. Cache under tools\singbox-cache\ so repeat
    # builds reuse the download — version-pinned, so this cache never
    # needs manual invalidation.
    $singBoxCache = Join-Path $Root "tools\singbox-cache"
    New-Item -ItemType Directory -Force -Path $singBoxCache | Out-Null
    $zipName = "sing-box-$SingBoxVersion-windows-amd64.zip"
    $zipPath = Join-Path $singBoxCache $zipName
    $extractDir = Join-Path $singBoxCache "sing-box-$SingBoxVersion-windows-amd64"
    $cachedExe = Join-Path $extractDir "sing-box.exe"

    if (-not (Test-Path $cachedExe)) {
        if (-not (Test-Path $zipPath)) {
            $dlUrl = "https://github.com/SagerNet/sing-box/releases/download/v$SingBoxVersion/$zipName"
            Write-Host "       Downloading upstream sing-box v$SingBoxVersion from $dlUrl..." -ForegroundColor Gray
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            try {
                Invoke-WebRequest -Uri $dlUrl -OutFile $zipPath -UseBasicParsing
            } catch {
                Write-Host "       ERROR: Download failed: $_" -ForegroundColor Red
                if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
                throw "sing-box download failed. Check https://github.com/SagerNet/sing-box/releases/tag/v$SingBoxVersion"
            }
        }
        if (Test-Path $extractDir) { Remove-Item -Recurse -Force $extractDir }
        Expand-Archive -Path $zipPath -DestinationPath $singBoxCache -Force
        if (-not (Test-Path $cachedExe)) {
            throw "sing-box.exe not found inside $zipName after extraction"
        }
    }

    Copy-Item $cachedExe (Join-Path $DistDir "sing-box.exe") -Force
    $sbSize = [math]::Round((Get-Item $cachedExe).Length / 1MB, 1)
    Write-Host "       Bundled upstream sing-box v$SingBoxVersion ($sbSize MB)" -ForegroundColor Green
}

# ── Bundle wgturn-cli.exe (Phase 1 of emergency channel integration) ──
# Built from PavelLizunov/wgturn-core. Provides the wgturn-cli.exe binary
# that VPNRouter will invoke via EmergencyChannelEngine (Phase 2) to
# tunnel WireGuard through VK Calls' TURN infrastructure when standard
# VPN protocols are blocked (РКН white-list mode emergency channel).
#
# Phase 1 just ships the artifact in app/bin/. No UI, no engine wiring.
#
# Source resolution order (first hit wins):
#   1. $env:WGTURN_CORE_DIR  — explicit dev override
#   2. tools/wgturn-cli-cache/wgturn-core/  — local cache
#   3. gh repo clone PavelLizunov/wgturn-core → cache  — auto-clone
#                                                       (requires gh auth)
# If none works the step is skipped with a warning; the build still
# produces a valid (but wgturn-less) artifact.
Write-Host "       Bundling wgturn-cli.exe..." -ForegroundColor Yellow
$WgturnBinDir = Join-Path $DistDir "bin"
$WgturnCoreDir = $env:WGTURN_CORE_DIR
if (-not $WgturnCoreDir -or -not (Test-Path "$WgturnCoreDir\cmd\wgturn-cli")) {
    $cacheRoot = Join-Path $Root "tools\wgturn-cli-cache"
    $cacheDir  = Join-Path $cacheRoot "wgturn-core"
    if (Test-Path "$cacheDir\cmd\wgturn-cli") {
        $WgturnCoreDir = $cacheDir
    } elseif (Get-Command gh -ErrorAction SilentlyContinue) {
        Write-Host "       Cloning PavelLizunov/wgturn-core into tools\wgturn-cli-cache\..." -ForegroundColor Gray
        New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
        & gh repo clone PavelLizunov/wgturn-core $cacheDir 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path "$cacheDir\cmd\wgturn-cli")) {
            $WgturnCoreDir = $cacheDir
        }
    }
}
$hasGo = $null -ne (Get-Command go -ErrorAction SilentlyContinue)
$wgturnSrcOk = $WgturnCoreDir -and (Test-Path (Join-Path $WgturnCoreDir "cmd\wgturn-cli"))
if ($wgturnSrcOk -and $hasGo) {
    New-Item -ItemType Directory -Force -Path $WgturnBinDir | Out-Null
    Push-Location $WgturnCoreDir
    try {
        $WgturnSha = (git rev-parse --short=12 HEAD 2>&1 | Out-String).Trim()
        if (-not $WgturnSha -or $WgturnSha -match '\s') { $WgturnSha = "unknown" }
        $env:GOOS = "windows"
        $env:GOARCH = "amd64"
        $env:CGO_ENABLED = "0"
        $wgturnOut = Join-Path $WgturnBinDir "wgturn-cli.exe"
        $wgturnLdflags = "-s -w -X main.version=$WgturnSha"
        go build -trimpath "-ldflags=$wgturnLdflags" -o $wgturnOut ./cmd/wgturn-cli 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "       wgturn-cli build FAILED (exit $LASTEXITCODE) - skipped." -ForegroundColor Yellow
        } else {
            $wgSize = [math]::Round((Get-Item $wgturnOut).Length / 1MB, 1)
            Write-Host "       wgturn-cli.exe bundled at app\bin\ (sha $WgturnSha, $wgSize MB)" -ForegroundColor Green
        }
    } finally {
        Pop-Location
    }
} else {
    Write-Host "       wgturn-cli.exe SKIPPED (set WGTURN_CORE_DIR or clone into tools\wgturn-cli-cache\, requires 'go' on PATH)" -ForegroundColor Yellow
}

# ── Zapret (DPI bypass) — downloaded on demand from Flowseal/zapret-discord-youtube ──
Write-Host "       Zapret: downloaded on demand (not bundled)" -ForegroundColor Gray

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

# ── Create UPDATE ZIP (v2.29.0-r6 bootstrap layout) ──
# Layout:
#   VPNRouter.GUI.exe       ← Go stub at ROOT (not locked, copyable by
#                              broken pre-r6 ApplyUpdate)
#   _bootstrap/             ← all app DLLs + sing-box + profiles
#       VPNRouter.App.exe
#       VPNRouter.Core.dll
#       hostfxr.dll  ← .NET runtime, locked at copy time but goes to a
#                      fresh subdir → no conflict
#       ... rest ...
#       sing-box.exe
#       profiles/
#       README.txt
#
# Why this layout: pre-r6 ApplyUpdateWindows did file copy in-process.
# Many runtime DLLs were mapped into the running .NET app, .NET on
# Windows refuses to overwrite mapped files, the .bak rename fallback
# also failed silently for files opened without FILE_SHARE_DELETE.
# Result: ~10% of files stayed old, app relaunch loaded a mixed-version
# DLL set, AppVersion still showed the old number. Half the user base
# couldn't update. r6 fix:
#   * Top-level VPNRouter.GUI.exe is a freestanding Go binary, never
#     mapped by .NET, never locked. Copy succeeds via plain File.Copy.
#   * Everything else lives in `_bootstrap/`. Pre-r6 ApplyUpdate walks
#     extractedDir recursively and copies each file to appDir at
#     relative paths → DLLs land at appDir/_bootstrap/<dll>. None of
#     those targets exist before the copy → no conflicts → no silent
#     skips.
#   * Pre-r6 ApplyUpdate then Process.Start's appDir/VPNRouter.GUI.exe.
#     That's the NEW Go stub (just replaced). The new stub detects
#     `_bootstrap/` next to itself, waits for the parent VPNRouter.App
#     to exit (now nothing locked), xcopies _bootstrap/* over appDir
#     overwriting, deletes _bootstrap/, launches the freshly-replaced
#     VPNRouter.App.exe.
#
# r5+ ApplyUpdateWindows (detached .cmd helper) also handles this layout
# correctly because the helper xcopies extractedDir as-is and the same
# bootstrap recovery runs in the relaunch.
Write-Host "[9/9] Creating update ZIP (bootstrap layout)..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $UpdateDir | Out-Null

# Top-level: ONLY the Go stub. This is the bootstrap entry point.
$updateGuiStub = Join-Path $DistDir "VPNRouter.GUI.exe"
if (-not (Test-Path $updateGuiStub)) {
    throw "Update ZIP build: VPNRouter.GUI.exe missing from $DistDir - Go stub not built?"
}
Copy-Item $updateGuiStub $UpdateDir
Write-Host "       VPNRouter.GUI.exe -> ROOT (bootstrap entry)" -ForegroundColor Gray

# Bootstrap subdir: everything else.
$BootstrapDir = Join-Path $UpdateDir "_bootstrap"
New-Item -ItemType Directory -Force -Path $BootstrapDir | Out-Null

# Copy app-only files from dist (using fd file list as reference)
# EXCEPT VPNRouter.GUI.exe — that one stays at ROOT.
$fdFileNames = (Get-ChildItem $FdDir -File).Name | Sort-Object -Unique
$updateFileCount = 0
foreach ($name in $fdFileNames) {
    if ($name -eq "VPNRouter.GUI.exe") { continue }  # already at root
    $src = Join-Path $DistDir $name
    if (Test-Path $src) {
        Copy-Item $src $BootstrapDir
        $updateFileCount++
    }
}
# Include sing-box.exe (version may change between releases)
$singBoxInDist = Join-Path $DistDir "sing-box.exe"
if (Test-Path $singBoxInDist) {
    Copy-Item $singBoxInDist $BootstrapDir
    $updateFileCount++
    Write-Host "       sing-box.exe included in update (under _bootstrap/)" -ForegroundColor Gray
}
# Include wgturn-cli.exe (emergency channel binary, Phase 1)
$wgturnInDist = Join-Path $WgturnBinDir "wgturn-cli.exe"
if (Test-Path $wgturnInDist) {
    $BootstrapBinDir = Join-Path $BootstrapDir "bin"
    New-Item -ItemType Directory -Force -Path $BootstrapBinDir | Out-Null
    Copy-Item $wgturnInDist $BootstrapBinDir
    $updateFileCount++
    Write-Host "       wgturn-cli.exe included in update (under _bootstrap/bin/)" -ForegroundColor Gray
}
# Zapret: downloaded on demand, not in update package
# Also include profiles and README under _bootstrap/.
$UpdateProfilesDst = Join-Path $BootstrapDir "profiles"
if (Test-Path $ProfilesSrc) {
    New-Item -ItemType Directory -Force -Path $UpdateProfilesDst | Out-Null
    Copy-Item "$ProfilesSrc\*" $UpdateProfilesDst -Recurse
}
Copy-Item $ReadmePath $BootstrapDir

Write-Host "       Update package: 1 stub at root + $updateFileCount files in _bootstrap/" -ForegroundColor Gray

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

# ── Generate SHA256 checksums for both ZIPs (uploaded alongside for verification) ──
$InstallShaPath = "$InstallZipPath.sha256"
$UpdateShaPath = "$UpdateZipPath.sha256"
(Get-FileHash -Algorithm SHA256 $InstallZipPath).Hash.ToLower() | Set-Content $InstallShaPath -NoNewline
(Get-FileHash -Algorithm SHA256 $UpdateZipPath).Hash.ToLower() | Set-Content $UpdateShaPath -NoNewline
Write-Host "SHA256 (install): $(Get-Content $InstallShaPath)" -ForegroundColor Gray
Write-Host "SHA256 (update):  $(Get-Content $UpdateShaPath)" -ForegroundColor Gray
Write-Host ""

Write-Host "Package contents:" -ForegroundColor Gray
Get-ChildItem $DistDir -Recurse | ForEach-Object {
    $rel = $_.FullName.Replace($DistDir, "").TrimStart("\")
    if ($_.PSIsContainer) { "  $rel\" } else { "  $rel  ($([math]::Round($_.Length/1KB)) KB)" }
}

# ── Optional: local Android APK build (-AndroidAlso) ──
# Contributor convenience. CI is authoritative — this just lets a dev
# validate Android changes before pushing the tag. Failures here never
# fail the script; Windows artifacts above already exist on disk.
$AndroidBuilt = $false
$ApkPath = $null
$ApkShaPath = $null

if ($AndroidAlso) {
    Write-Host ""
    Write-Host "=== Android APK build (-AndroidAlso) ===" -ForegroundColor Cyan

    # Load .env.local (KEY=VALUE per line). Existing env vars win.
    $envLocal = Join-Path $Root ".env.local"
    if (Test-Path $envLocal) {
        Get-Content $envLocal | ForEach-Object {
            if ($_ -match '^\s*([A-Z_][A-Z0-9_]*)\s*=\s*(.*?)\s*$') {
                $k = $Matches[1]
                $v = $Matches[2].Trim('"').Trim("'")
                if (-not (Get-Item "Env:$k" -ErrorAction SilentlyContinue)) {
                    Set-Item "Env:$k" -Value $v
                }
            }
        }
        Write-Host "       Loaded $envLocal (existing env vars take precedence)" -ForegroundColor Gray
    }

    # Prereq check — JAVA_HOME, ANDROID_HOME, dotnet workload 'android'.
    $issues = @()
    if (-not $env:JAVA_HOME) {
        $issues += "JAVA_HOME not set (point at JDK 17 - e.g. Temurin)"
    } elseif (-not (Test-Path $env:JAVA_HOME)) {
        $issues += "JAVA_HOME points at non-existent path: $env:JAVA_HOME"
    }
    if (-not $env:ANDROID_HOME) {
        $issues += "ANDROID_HOME not set (point at Android SDK root)"
    } elseif (-not (Test-Path $env:ANDROID_HOME)) {
        $issues += "ANDROID_HOME points at non-existent path: $env:ANDROID_HOME"
    }
    try {
        $workloadOutput = & dotnet workload list 2>&1 | Out-String
        if ($workloadOutput -notmatch '(?im)^\s*android\b') {
            $issues += "dotnet workload 'android' not installed (run: dotnet workload install android)"
        }
    } catch {
        $issues += "could not run 'dotnet workload list' to verify Android workload: $_"
    }

    if ($issues.Count -gt 0) {
        Write-Host "Android build SKIPPED - prerequisites missing:" -ForegroundColor Yellow
        foreach ($i in $issues) { Write-Host "  - $i" -ForegroundColor Yellow }
        Write-Host "Windows artifacts above are still valid; only the APK was skipped." -ForegroundColor Yellow
    } else {
        # Resolve signing keystore. Order: env vars → .env.local (already
        # merged above) → vpnrouter.keystore next to the .csproj.
        $signingArgs = @()
        $hasKeystore = $false
        $keystoreSource = ""

        if ($env:ANDROID_KEYSTORE_PATH -and $env:ANDROID_KEYSTORE_PASSWORD -and (Test-Path $env:ANDROID_KEYSTORE_PATH)) {
            $keyAlias = if ($env:ANDROID_KEYSTORE_KEY_ALIAS) { $env:ANDROID_KEYSTORE_KEY_ALIAS } else { "vpnrouter" }
            $keyPass  = if ($env:ANDROID_KEYSTORE_KEY_PASSWORD) { $env:ANDROID_KEYSTORE_KEY_PASSWORD } else { $env:ANDROID_KEYSTORE_PASSWORD }
            $signingArgs = @(
                "-p:AndroidSigningKeyStore=$($env:ANDROID_KEYSTORE_PATH)",
                "-p:AndroidSigningStorePass=$($env:ANDROID_KEYSTORE_PASSWORD)",
                "-p:AndroidSigningKeyAlias=$keyAlias",
                "-p:AndroidSigningKeyPass=$keyPass"
            )
            $hasKeystore = $true
            $keystoreSource = "ANDROID_KEYSTORE_PATH"
        } else {
            $csprojKeystore = Join-Path $Root "VPNRouter.Android\vpnrouter.keystore"
            if (Test-Path $csprojKeystore) {
                # csproj defaults pick this up via <AndroidSigningKeyStore>
                # vpnrouter.keystore</AndroidSigningKeyStore>. The csproj
                # does NOT specify a password — without env vars dotnet
                # prompts interactively, which fails under -NonInteractive.
                # So still require ANDROID_KEYSTORE_PASSWORD to be set.
                if ($env:ANDROID_KEYSTORE_PASSWORD) {
                    $keyAlias = if ($env:ANDROID_KEYSTORE_KEY_ALIAS) { $env:ANDROID_KEYSTORE_KEY_ALIAS } else { "vpnrouter" }
                    $keyPass  = if ($env:ANDROID_KEYSTORE_KEY_PASSWORD) { $env:ANDROID_KEYSTORE_KEY_PASSWORD } else { $env:ANDROID_KEYSTORE_PASSWORD }
                    $signingArgs = @(
                        "-p:AndroidSigningStorePass=$($env:ANDROID_KEYSTORE_PASSWORD)",
                        "-p:AndroidSigningKeyAlias=$keyAlias",
                        "-p:AndroidSigningKeyPass=$keyPass"
                    )
                    $hasKeystore = $true
                    $keystoreSource = "VPNRouter.Android\vpnrouter.keystore"
                }
            }
        }

        if (-not $hasKeystore) {
            Write-Host "Android build SKIPPED - no signing keystore available." -ForegroundColor Yellow
            Write-Host "  Provide via env vars:" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_PATH         = path to .keystore / .jks" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_PASSWORD     = store password" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_KEY_ALIAS    = alias (default: vpnrouter)" -ForegroundColor Yellow
            Write-Host "    ANDROID_KEYSTORE_KEY_PASSWORD = key password (default: store password)" -ForegroundColor Yellow
            Write-Host "  Or place the same keys in .env.local at the repo root." -ForegroundColor Yellow
            Write-Host "  Production keystore must match the one used by CI for auto-update to work." -ForegroundColor Yellow
        } else {
            Write-Host "Signing source: $keystoreSource" -ForegroundColor Gray
            Write-Host "Building APK (Release, android-arm64)..." -ForegroundColor Yellow

            $publishArgs = @(
                "publish", "$Root\VPNRouter.Android\VPNRouter.Android.csproj",
                "-c", "Release",
                "-p:RuntimeIdentifiers=android-arm64",
                "-p:AndroidEnableProfiledAot=false"
            ) + $signingArgs

            & dotnet @publishArgs
            $apkExit = $LASTEXITCODE

            if ($apkExit -ne 0) {
                Write-Host "Android build FAILED (dotnet publish exit $apkExit). Windows artifacts still valid." -ForegroundColor Red
            } else {
                $signedApk = Get-ChildItem -Path "$Root\VPNRouter.Android\bin\Release" -Recurse -Filter "*-Signed.apk" -ErrorAction SilentlyContinue |
                             Sort-Object LastWriteTime -Descending |
                             Select-Object -First 1
                if (-not $signedApk) {
                    Write-Host "Android build succeeded but no *-Signed.apk found under VPNRouter.Android\bin\Release\." -ForegroundColor Red
                } else {
                    $ApkName = "VPNRouter-v$Version-android-arm64.apk"
                    $ApkPath = Join-Path $Root $ApkName
                    Copy-Item $signedApk.FullName $ApkPath -Force

                    $ApkShaPath = "$ApkPath.sha256"
                    (Get-FileHash -Algorithm SHA256 $ApkPath).Hash.ToLower() | Set-Content $ApkShaPath -NoNewline

                    $apkSize = [math]::Round((Get-Item $ApkPath).Length / 1MB, 1)
                    Write-Host "APK: $ApkPath ($apkSize MB)" -ForegroundColor Green
                    Write-Host "SHA256 (apk): $(Get-Content $ApkShaPath)" -ForegroundColor Gray
                    $AndroidBuilt = $true
                }
            }
        }
    }
}

# ── Final artifact summary ──
Write-Host ""
Write-Host "=== Artifacts ===" -ForegroundColor Cyan
Write-Host "  Windows install ZIP : $InstallZipName" -ForegroundColor White
Write-Host "  Windows update ZIP  : $UpdateZipName" -ForegroundColor White
if ($AndroidAlso) {
    if ($AndroidBuilt) {
        Write-Host "  Android APK         : $(Split-Path $ApkPath -Leaf)" -ForegroundColor White
    } else {
        Write-Host "  Android APK         : SKIPPED (see warnings above)" -ForegroundColor Yellow
    }
}

# ── Upload to GitHub Releases (optional) ──
if ($Upload) {
    Write-Host ""
    Write-Host "Uploading to GitHub Releases..." -ForegroundColor Yellow

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "       ERROR: gh CLI not found. Install: winget install GitHub.cli" -ForegroundColor Red
    } else {
        $tag = "v$Version"

        # Build the asset list. APK + sha join only if the local Android
        # build above produced them; missing keystore / missing prereqs
        # silently fall through to a Windows-only upload (CI will publish
        # the APK on tag push regardless).
        $releaseAssets = @($InstallZipPath, $UpdateZipPath, $InstallShaPath, $UpdateShaPath)
        if ($AndroidBuilt) {
            $releaseAssets += @($ApkPath, $ApkShaPath)
            Write-Host "       Including local Android APK in release assets" -ForegroundColor Gray
        }

        gh release create $tag $releaseAssets `
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
