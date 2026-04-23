<#
.SYNOPSIS
    Downloads an upstream sing-box prebuilt binary and installs it for VPNRouter.
.DESCRIPTION
    v2.27.2: switched from custom-rebuilt sing-box (Go source + specific build
    tags) to the official upstream binaries from SagerNet/sing-box releases.

    Rationale for the switch:
      - Upstream 1.13+ ships with_clash_api + with_utls + with_quic baked
        in by default — the exact tags VPNRouter needs for Reality
        fingerprinting and hot-reload via Clash API.
      - Eliminates "custom build" as a variable when diagnosing weird
        sing-box behaviour (was biting us on YouTube drops in v2.27.1 —
        we couldn't trivially tell whether a quirk was in sing-box or
        in our rebuild's Go version / tag combo).
      - Upstream binaries are signed and reproducible; our custom build
        was neither.
      - ~12 MB size increase per platform (acceptable tradeoff).
      - Linux CI and macOS build-mac.sh already use upstream for the same
        reasons — Windows was the odd one out. This script keeps the
        manual/local Windows release workflow aligned with the automated
        Linux + macOS workflows.

    If you REALLY need a custom rebuild (e.g. to experiment with a tag
    combo upstream doesn't ship), check git history for the Go-source
    version of this script — tag v2.27.1 still has it.

.PARAMETER Version
    sing-box version tag to download (e.g. "1.13.10"). Required.
    Defaults to whatever the release notes of this VPNRouter version
    ship with, but you can override for local experiments.
.PARAMETER Install
    Copy downloaded binary to %ProgramData%\VPNRouter\bin\sing-box.exe
    (requires admin + VPN stopped).
.EXAMPLE
    .\build-singbox.ps1 -Version "1.13.10"
    .\build-singbox.ps1 -Version "1.13.10" -Install
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$Version,
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$CacheDir = Join-Path $Root "tools\singbox-cache"
$ZipName = "sing-box-$Version-windows-amd64.zip"
$ZipPath = Join-Path $CacheDir $ZipName
$ExtractDir = Join-Path $CacheDir "sing-box-$Version-windows-amd64"
$OutputExe = Join-Path $CacheDir "sing-box.exe"
$InstallPath = "$env:ProgramData\VPNRouter\bin\sing-box.exe"
$DownloadUrl = "https://github.com/SagerNet/sing-box/releases/download/v$Version/$ZipName"

Write-Host "=== sing-box Upstream Download ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Source:  $DownloadUrl"
Write-Host ""

# ── Prepare cache dir ──
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

# ── Download zip (skip if cached) ──
if (Test-Path $ZipPath) {
    Write-Host "[1/3] Using cached download: $ZipPath" -ForegroundColor Yellow
} else {
    Write-Host "[1/3] Downloading $ZipName..." -ForegroundColor Yellow
    try {
        # Use TLS 1.2+ explicitly — old PowerShell defaults to SSL3/TLS1.0
        # which GitHub rejects.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath -UseBasicParsing
    } catch {
        Write-Host "ERROR: Download failed: $_" -ForegroundColor Red
        Write-Host "       Check: https://github.com/SagerNet/sing-box/releases/tag/v$Version" -ForegroundColor Gray
        if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
        exit 1
    }

    $sizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    Write-Host "       Downloaded: $sizeMB MB" -ForegroundColor Gray
}

# ── Extract ──
Write-Host "[2/3] Extracting..." -ForegroundColor Yellow
if (Test-Path $ExtractDir) { Remove-Item -Recurse -Force $ExtractDir }
Expand-Archive -Path $ZipPath -DestinationPath $CacheDir -Force

$extractedExe = Join-Path $ExtractDir "sing-box.exe"
if (-not (Test-Path $extractedExe)) {
    Write-Host "ERROR: Expected $extractedExe inside zip, but it's missing." -ForegroundColor Red
    Write-Host "       Zip contents:" -ForegroundColor Gray
    Get-ChildItem $ExtractDir -Recurse | ForEach-Object { Write-Host "         $($_.FullName.Replace($ExtractDir, ''))" -ForegroundColor Gray }
    exit 1
}

Copy-Item $extractedExe $OutputExe -Force
$size = [math]::Round((Get-Item $OutputExe).Length / 1MB, 1)
Write-Host "       Extracted: $OutputExe ($size MB)" -ForegroundColor Green

# ── Verify ──
$versionOutput = & $OutputExe version 2>&1
Write-Host "       $($versionOutput[0])" -ForegroundColor Gray
if ($versionOutput.Count -gt 2) { Write-Host "       $($versionOutput[2])" -ForegroundColor Gray }

# Verify we got the version we asked for (not a redirect to latest etc.)
$expectedVersionLine = "sing-box version $Version"
if ($versionOutput[0] -notlike "*$Version*") {
    Write-Host "WARN: Binary reports '$($versionOutput[0])' but expected '$expectedVersionLine'" -ForegroundColor Yellow
    Write-Host "      (continuing anyway — check if upstream changed their version string)" -ForegroundColor Yellow
}

# ── Install ──
if ($Install) {
    Write-Host "[3/3] Installing to $InstallPath..." -ForegroundColor Yellow

    # Check if sing-box is running (would lock the exe)
    $running = Get-Process -Name "sing-box" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "       ERROR: sing-box is running (PID $($running.Id)). Stop VPN first!" -ForegroundColor Red
        exit 1
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $InstallPath) | Out-Null
    Copy-Item $OutputExe $InstallPath -Force
    Write-Host "       Installed!" -ForegroundColor Green
} else {
    Write-Host "[3/3] Skipping install (use -Install flag)" -ForegroundColor Gray
    Write-Host "       Binary at: $OutputExe" -ForegroundColor Gray
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
