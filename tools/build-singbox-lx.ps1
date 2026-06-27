<#
.SYNOPSIS
    Build sing-box-lx (Leadaxe fork) with AmneziaWG (with_awg) from source, for the
    AmneziaWG-enabled VPNRouter candidate. Output is a drop-in `sing-box.exe`.
.DESCRIPTION
    Official sing-box can't carry AmneziaWG; the Leadaxe/sing-box-lx thin fork adds
    AWG2 as a `wireguard` endpoint behind the `with_awg` build tag (binary identity
    stays `sing-box`). This script clones BOTH the fork and its wireguard-go-awg2-lx
    submodule at PINNED commits, builds with the canonical lx tag set (verified to
    produce a binary whose AWG endpoint passes `check`), and writes sing-box.exe.

    Then bundle it WITHOUT touching the normal release path:
        powershell -File build.ps1 -Version "X.Y.Z-rN" -SingBoxPath <OutputPath> -Upload

    NOTE: this is a third-party fork built from source. Vet the diff (it is thin:
    XHTTP + AWG2) and keep the pins below in sync with the implementation plan
    (plans/amneziawg-fork-implementation-plan-2026-06-27.md). Requires Go (>=1.24).
.PARAMETER OutputPath
    Where to write the built sing-box.exe. Default: .\publish\sing-box-lx.exe
.PARAMETER WorkDir
    Scratch dir for the clone+build. Default: a temp dir.
#>
param(
    [string]$OutputPath = "$PSScriptRoot\..\publish\sing-box-lx.exe",
    [string]$WorkDir = (Join-Path $env:TEMP "vpnrouter-singbox-lx")
)
$ErrorActionPreference = 'Stop'

# ── Pinned commits (verified 2026-06-27 on Go 1.26.1; AWG endpoint passes `check`) ──
$LX_REPO   = 'https://github.com/Leadaxe/sing-box-lx'
$LX_COMMIT = 'c7a2592e750406ade9ebaae1d0fdb7482fc0773e'
$WG_REPO   = 'https://github.com/Leadaxe/wireguard-go-awg2-lx'
$WG_BRANCH = 'lx'
$WG_COMMIT = '0c0c10b5d3236796bd3832a6813223d6dc7d0bb1'
# Canonical lx tag set (see Makefile.lx / SPECS/004) — feature tags + our two downstream.
$TAGS = 'with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg'
$VER  = '1.13.13-lx-awg'

if (-not (Get-Command go -ErrorAction SilentlyContinue)) { throw "Go toolchain not found on PATH." }

# Helper: run git WITHOUT redirecting its stderr (PS 5.1 wraps native stderr as a
# NativeCommandError and trips -ErrorAction Stop even on exit 0); check the exit code.
function Invoke-Git { param([string[]]$GitArgs)
    & git @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed ($LASTEXITCODE)" }
}

$src = Join-Path $WorkDir 'sing-box-lx'
Write-Host "[1/4] Clone sing-box-lx @ $LX_COMMIT" -ForegroundColor Yellow
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Invoke-Git @('clone', '--quiet', $LX_REPO, $src)
Invoke-Git @('-C', $src, 'checkout', '--quiet', $LX_COMMIT)

Write-Host "[2/4] Clone wireguard-go-awg2-lx @ $WG_COMMIT (submodule path)" -ForegroundColor Yellow
# The fork's `git submodule update` trips over its apple/android client submodules, so
# clone the one we need (the AWG wireguard-go) directly into the replace path.
$wg = Join-Path $src 'submodules\wireguard-go'
if (Test-Path $wg) { Remove-Item -Recurse -Force $wg }
Invoke-Git @('clone', '--quiet', '-b', $WG_BRANCH, $WG_REPO, $wg)
Invoke-Git @('-C', $wg, 'checkout', '--quiet', $WG_COMMIT)

Write-Host "[3/4] go build -tags $TAGS" -ForegroundColor Yellow
$ldflags = "-checklinkname=0 -X github.com/sagernet/sing-box/constant.Version=$VER"
Push-Location $src
try {
    $env:CGO_ENABLED = '0'
    go build -trimpath -tags $TAGS -ldflags $ldflags -o 'sing-box.exe' ./cmd/sing-box
    if ($LASTEXITCODE -ne 0) { throw "go build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath) | Out-Null
Copy-Item (Join-Path $src 'sing-box.exe') $OutputPath -Force

Write-Host "[4/4] Verify" -ForegroundColor Yellow
# The version STRING is forged via -ldflags -X (line above), so it proves
# nothing about the build. The "Tags:" line is what Go derives from the REAL
# build tags and is NOT forgeable — assert with_awg + with_xhttp are actually
# compiled in. Go silently ignores unknown -tags, so without this a dropped tag
# / unresolved wireguard-go replace yields a forged-but-feature-less binary that
# ships green and then FATALs every AWG/xhttp config at runtime.
$verOut = & $OutputPath version 2>&1 | Out-String
Write-Host $verOut.Trim()
$tagsLine = ($verOut -split "`n" | Where-Object { $_ -match '^\s*Tags:' }) -join ''
foreach ($needed in @('with_awg', 'with_xhttp')) {
    if ($tagsLine -notmatch [regex]::Escape($needed)) {
        throw "FATAL: built sing-box-lx is MISSING build tag '$needed' (Tags line: '$($tagsLine.Trim())'). " +
              "The binary would reject AWG/XHTTP configs at runtime. Do NOT bundle it. " +
              "Check the wireguard-go replace path + that `$TAGS includes $needed."
    }
}
# Belt-and-suspenders: a feature-less binary also FATALs `check` on an AWG config.
# Include AWG-only fields (jc/jmin/jmax) so `check` exercises with_awg, not just the
# base wireguard endpoint. Write UTF-8 WITHOUT BOM — PS5.1 `Set-Content -Encoding utf8`
# prepends a BOM that some JSON loaders reject, false-failing a good binary.
$awgProbe = Join-Path ([System.IO.Path]::GetTempPath()) "awg-probe-$PID.json"
$awgProbeJson = @'
{"endpoints":[{"type":"wireguard","tag":"proxy","mtu":1280,"jc":4,"jmin":40,"jmax":70,"address":["10.0.0.2/32"],
"private_key":"aGVsbG8taGVsbG8taGVsbG8taGVsbG8taGVsbG8tMDA=","peers":[{"address":"127.0.0.1",
"port":51820,"public_key":"aGVsbG8taGVsbG8taGVsbG8taGVsbG8taGVsbG8tMDA=","allowed_ips":["0.0.0.0/0"]}]}]}
'@
[System.IO.File]::WriteAllText($awgProbe, $awgProbeJson, (New-Object System.Text.UTF8Encoding $false))
try {
    & $OutputPath check -c $awgProbe 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "FATAL: sing-box-lx rejected a minimal AWG endpoint config (exit $LASTEXITCODE) — with_awg not functional." }
    Write-Host "Verified: with_awg + with_xhttp present; AWG endpoint config accepted." -ForegroundColor Green
} finally { Remove-Item -Force $awgProbe -ErrorAction SilentlyContinue }
Write-Host "Built: $OutputPath" -ForegroundColor Green
Write-Host "Bundle it:  powershell -File build.ps1 -Version <X.Y.Z-rN> -SingBoxPath `"$OutputPath`" -Upload" -ForegroundColor Cyan
