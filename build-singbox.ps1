<#
.SYNOPSIS
    Builds a minimal sing-box binary from source with only required features.
.DESCRIPTION
    Clones/updates sing-box source, checks out the specified version tag,
    and builds with minimal Go build tags (with_utls + with_clash_api only).

    Full official build: ~35 MB (8 tags: gvisor, quic, wireguard, tailscale, etc.)
    VPNRouter build: ~23 MB (3 tags: utls + clash_api + quic)

    VPNRouter needs:
    - with_utls       : uTLS fingerprinting for Reality/TLS
    - with_clash_api  : Clash REST API for hot-reload config
    - with_quic       : QUIC transport (Hysteria2, TUIC, HTTP/3 — required for custom configs)

    NOT needed (stack="system", not gVisor; no WireGuard/Tailscale protocols):
    - with_gvisor, with_wireguard, with_tailscale, with_dhcp, with_acme
.PARAMETER Version
    sing-box version tag to build (e.g. "1.12.21"). Required.
.PARAMETER Install
    Copy built binary to %ProgramData%\VPNRouter\bin\sing-box.exe (requires admin + VPN stopped)
.EXAMPLE
    .\build-singbox.ps1 -Version "1.12.21"
    .\build-singbox.ps1 -Version "1.12.21" -Install
#>
param(
    [Parameter(Mandatory=$true)]
    [string]$Version,
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$SrcDir = Join-Path $Root "tools\sing-box-src"
$Tags = "with_utls,with_clash_api,with_quic"
$OutputExe = Join-Path $SrcDir "sing-box.exe"
$InstallPath = "$env:ProgramData\VPNRouter\bin\sing-box.exe"

Write-Host "=== sing-box Minimal Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Tags:    $Tags"
Write-Host ""

# ── Check Go ──
$goExe = Get-Command go -ErrorAction SilentlyContinue
if (-not $goExe) {
    Write-Host "ERROR: Go not found. Install: winget install GoLang.Go" -ForegroundColor Red
    exit 1
}
Write-Host "[1/4] Go: $(go version)" -ForegroundColor Yellow

# ── Clone or update source ──
Write-Host "[2/4] Preparing source..." -ForegroundColor Yellow
if (-not (Test-Path (Join-Path $SrcDir ".git"))) {
    Write-Host "       Cloning sing-box..." -ForegroundColor Gray
    git clone https://github.com/SagerNet/sing-box.git $SrcDir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Clone failed" }
} else {
    Write-Host "       Fetching latest tags..." -ForegroundColor Gray
    Push-Location $SrcDir
    $fetchOutput = git fetch --tags 2>&1
    Pop-Location
}

Push-Location $SrcDir
$checkoutOutput = git checkout "v$Version" 2>&1
if ($LASTEXITCODE -ne 0) {
    Pop-Location
    throw "Tag v$Version not found. Check: https://github.com/SagerNet/sing-box/tags"
}
Write-Host "       Checked out v$Version" -ForegroundColor Gray

# ── Build ──
Write-Host "[3/4] Building (this may take a minute)..." -ForegroundColor Yellow
$env:GOPROXY = "direct"
$env:GOOS = "windows"
$env:GOARCH = "amd64"

$ldflags = "-s -w -buildid= -X 'github.com/sagernet/sing-box/constant.Version=$Version'"

go build -v -trimpath `
    -tags $Tags `
    -ldflags $ldflags `
    -o sing-box.exe `
    ./cmd/sing-box 2>&1 | Select-Object -Last 5

if ($LASTEXITCODE -ne 0) {
    Pop-Location
    throw "Build failed"
}
Pop-Location

$size = [math]::Round((Get-Item $OutputExe).Length / 1MB, 1)
Write-Host "       Built: $OutputExe ($size MB)" -ForegroundColor Green

# ── Verify ──
$versionOutput = & $OutputExe version 2>&1
Write-Host "       $($versionOutput[0])" -ForegroundColor Gray
Write-Host "       $($versionOutput[2])" -ForegroundColor Gray

# ── Install ──
if ($Install) {
    Write-Host "[4/4] Installing to $InstallPath..." -ForegroundColor Yellow

    # Check if sing-box is running
    $running = Get-Process -Name "sing-box" -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "       ERROR: sing-box is running (PID $($running.Id)). Stop VPN first!" -ForegroundColor Red
        exit 1
    }

    New-Item -ItemType Directory -Force -Path (Split-Path $InstallPath) | Out-Null
    Copy-Item $OutputExe $InstallPath -Force
    Write-Host "       Installed!" -ForegroundColor Green
} else {
    Write-Host "[4/4] Skipping install (use -Install flag)" -ForegroundColor Gray
    Write-Host "       Binary at: $OutputExe" -ForegroundColor Gray
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
