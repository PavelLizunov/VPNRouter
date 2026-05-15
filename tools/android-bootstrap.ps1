# android-bootstrap.ps1 — reproducible toolchain installer for VPNRouter
# Android development. Meta-test #8 (`plans/android-development-methodology.md`)
# requires this script: «toolchain bootstrap works on fresh VM».
#
# Idempotent: run multiple times, skips already-installed parts.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/android-bootstrap.ps1
#   powershell -ExecutionPolicy Bypass -File tools/android-bootstrap.ps1 -SkipSdkInstall
#
# What it installs:
#   1. .NET 8 Android workload (if dotnet exists)
#   2. Temurin 17 JDK via Adoptium MSI (if JAVA_HOME unset)
#   3. Android SDK 34 cmdline-tools (if ANDROID_HOME unset)
#   4. Validates: ANDROID_HOME / JAVA_HOME / dotnet workload presence
#
# Verification:
#   After run, attempts test build:
#     dotnet build VPNRouter.Android/VPNRouter.Android.csproj \
#       -c Release /p:EnableAndroidTarget=true
#   If exits 0 — bootstrap succeeded.

[CmdletBinding()]
param(
    [switch]$SkipSdkInstall,
    [switch]$SkipJdkInstall,
    [switch]$SkipWorkload,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Write-Host "── VPNRouter Android Bootstrap ──" -ForegroundColor Cyan
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

# ── Step 1: .NET 8 SDK presence ──
Write-Host "`n[1/5] Checking .NET 8 SDK..."
try {
    $dotnetVersion = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet not in PATH" }
    Write-Host "  dotnet: $dotnetVersion" -ForegroundColor Green
}
catch {
    Write-Error "dotnet 8 SDK required. Install from https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

# ── Step 2: Android workload ──
Write-Host "`n[2/5] Checking dotnet android workload..."
if ($SkipWorkload -or $VerifyOnly) {
    Write-Host "  Skipped (flag)" -ForegroundColor Yellow
} else {
    $workloads = & dotnet workload list 2>&1
    if ($workloads -notmatch 'android') {
        Write-Host "  android workload not installed — installing..." -ForegroundColor Yellow
        Write-Host "  (this requires elevation OR --user-level)" -ForegroundColor Gray
        & dotnet workload install android
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "  dotnet workload install android failed — try running as admin"
        }
    } else {
        Write-Host "  android workload: installed" -ForegroundColor Green
    }
}

# ── Step 3: JDK ──
Write-Host "`n[3/5] Checking JDK..."
$jdkOk = $false
if ($env:JAVA_HOME -and (Test-Path "$env:JAVA_HOME\bin\javac.exe")) {
    $javacVer = & "$env:JAVA_HOME\bin\javac.exe" -version 2>&1
    Write-Host "  JAVA_HOME=$env:JAVA_HOME" -ForegroundColor Green
    Write-Host "  javac: $javacVer" -ForegroundColor Green
    $jdkOk = $true
}
if (-not $jdkOk -and -not ($SkipJdkInstall -or $VerifyOnly)) {
    Write-Host "  JDK not configured — methodology §8 requires Temurin 17." -ForegroundColor Yellow
    Write-Host "  Install: https://adoptium.net/temurin/releases/?version=17" -ForegroundColor Yellow
    Write-Host "  After install, set JAVA_HOME and rerun this script." -ForegroundColor Yellow
}

# ── Step 4: Android SDK ──
Write-Host "`n[4/5] Checking Android SDK..."
$sdkOk = $false
if ($env:ANDROID_HOME -and (Test-Path "$env:ANDROID_HOME\platforms")) {
    Write-Host "  ANDROID_HOME=$env:ANDROID_HOME" -ForegroundColor Green
    $platforms = Get-ChildItem "$env:ANDROID_HOME\platforms" -Directory | Select-Object -ExpandProperty Name
    Write-Host "  Installed platforms: $($platforms -join ', ')" -ForegroundColor Green
    $sdkOk = $true
}
if (-not $sdkOk -and -not ($SkipSdkInstall -or $VerifyOnly)) {
    Write-Host "  Android SDK not configured." -ForegroundColor Yellow
    Write-Host "  Methodology §8 setup:" -ForegroundColor Yellow
    Write-Host "    1. Download cmdline-tools: https://developer.android.com/studio#command-line-tools-only" -ForegroundColor Gray
    Write-Host "    2. Unzip to e.g. C:\Android\cmdline-tools\latest\" -ForegroundColor Gray
    Write-Host "    3. Set ANDROID_HOME to parent (C:\Android)" -ForegroundColor Gray
    Write-Host "    4. Run: sdkmanager 'platforms;android-34' 'build-tools;34.0.0'" -ForegroundColor Gray
    Write-Host "    5. Rerun this script to verify." -ForegroundColor Gray
}

# ── Step 5: Verify by building ──
Write-Host "`n[5/5] Verification build..."
if (-not $jdkOk -or -not $sdkOk) {
    Write-Warning "  Skipped — JDK or SDK missing. Fix above warnings first."
    exit 2
}

Push-Location $ProjectRoot
try {
    $env:DOTNET_NOLOGO = "true"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    Write-Host "  Building VPNRouter.Android (Release, EnableAndroidTarget=true)..." -ForegroundColor Gray
    & dotnet build VPNRouter.Android/VPNRouter.Android.csproj `
        -c Release `
        /p:EnableAndroidTarget=true `
        /p:AndroidSdkDirectory="$env:ANDROID_HOME" `
        /p:JavaSdkDirectory="$env:JAVA_HOME" `
        -v minimal 2>&1 | Select-Object -Last 10
    if ($LASTEXITCODE -eq 0) {
        Write-Host "`n✓ Bootstrap verified — Android build works." -ForegroundColor Green
    } else {
        Write-Error "`n✗ Verification build failed. See output above."
        exit 3
    }
}
finally {
    Pop-Location
}
