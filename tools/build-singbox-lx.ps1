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
# Canonical lx tag set (see Makefile.lx / SPECS/004) -- feature tags + our two downstream.
$TAGS = 'with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg'
$VER  = '1.13.13-lx-awg'

if (-not (Get-Command go -ErrorAction SilentlyContinue)) { throw "Go toolchain not found on PATH." }

# Helper: run git WITHOUT redirecting its stderr (PS 5.1 wraps native stderr as a
# NativeCommandError and trips -ErrorAction Stop even on exit 0); check the exit code.
function Invoke-Git { param([string[]]$GitArgs)
    & git @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed ($LASTEXITCODE)" }
}

# P2 supply-chain (2026-07-10): assert a checked-out repo's HEAD == the pinned
# commit. `git checkout <sha>` normally lands exactly there, but if a pin were
# ever a branch/tag name (or a rev that later moved) the build would silently
# produce a binary from an unpinned tree. Fail CLOSED so a drifted checkout can
# never be bundled + signed.
function Assert-GitHead { param([string]$RepoDir, [string]$Expected, [string]$Label)
    $head = (& git -C $RepoDir rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "git -C $RepoDir rev-parse HEAD failed ($LASTEXITCODE)" }
    if ($head -ne $Expected) {
        throw "$Label HEAD drift: expected $Expected, got $head. The pinned commit did not check out cleanly (moved tag/branch?); refusing to build an unpinned core."
    }
    Write-Host "       $Label HEAD pinned OK ($head)" -ForegroundColor DarkGray
}

$src = Join-Path $WorkDir 'sing-box-lx'
Write-Host "[1/4] Clone sing-box-lx @ $LX_COMMIT" -ForegroundColor Yellow
if (Test-Path $src) { Remove-Item -Recurse -Force $src }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Invoke-Git @('clone', '--quiet', $LX_REPO, $src)
Invoke-Git @('-C', $src, 'checkout', '--quiet', $LX_COMMIT)
Assert-GitHead -RepoDir $src -Expected $LX_COMMIT -Label 'sing-box-lx'

Write-Host "[2/4] Clone wireguard-go-awg2-lx @ $WG_COMMIT (submodule path)" -ForegroundColor Yellow
# The fork's `git submodule update` trips over its apple/android client submodules, so
# clone the one we need (the AWG wireguard-go) directly into the replace path.
$wg = Join-Path $src 'submodules\wireguard-go'
if (Test-Path $wg) { Remove-Item -Recurse -Force $wg }
Invoke-Git @('clone', '--quiet', '-b', $WG_BRANCH, $WG_REPO, $wg)
Invoke-Git @('-C', $wg, 'checkout', '--quiet', $WG_COMMIT)
Assert-GitHead -RepoDir $wg -Expected $WG_COMMIT -Label 'wireguard-go-awg2-lx'

# ── Patch: Go 1.26 Windows WSASendMsg regression (golang/go#77875) ──
# On Windows the wireguard-go StdNetBind send loop calls net.UDPConn.WriteMsgUDP
# with a NON-NIL, zero-length OOB (conn/control_default.go controlSize==0 ->
# msgsPool allocates make([]byte,0)). Go 1.26's internal/poll newWSAMsg lost the
# historic empty-OOB nil guard, so WSAMSG.Control.Buf becomes a non-nil zerobase
# pointer with Len==0 and WSASendMsg rejects the call with WSAEFAULT
# ("The system detected an invalid pointer address ..."). Every AmneziaWG
# handshake-initiation send then fails and the tunnel never establishes on
# Windows (diag 2026-06-28: 0 handshakes, repeated wsasendmsg WSAEFAULT every 5s).
# sing-box-lx uses conn.NewStdNetBind directly for a detour-less AWG endpoint
# (transport/wireguard/endpoint.go), so this path is ALWAYS hit. Fix per
# golang/go#77875: pass nil for an empty OOB so Control.Buf is NULL. Verified
# end-to-end (handshake send no longer WSAEFAULTs). Remove once the bundled Go
# toolchain carries the upstream fix (>=1.26.2 / 1.27) or the fork guards it.
Write-Host "[2.5/4] Patch conn/bind_std.go (golang/go#77875 WSAEFAULT, send+recv)" -ForegroundColor Yellow
$bindStd = Join-Path $wg 'conn\bind_std.go'
if (-not (Test-Path $bindStd)) { throw "FATAL: $bindStd not found (fork layout changed); re-vet the WSAEFAULT patch." }
$bindSrc = Get-Content -Raw $bindStd

# (1) ROOT FIX for BOTH directions. The pooled per-message OOB is allocated as
# make([]byte, controlSize); on Windows (and macOS) controlSize==0, so it is a
# NON-NIL zero-length slice. Go 1.26 WSASendMsg AND WSARecvMsg reject that
# (golang/go#77875: unsafe.SliceData(make([]byte,0)) is a non-nil pointer with
# Len 0). Leave OOB nil when there is no control data so Control.Buf is NULL.
# This single change fixes the send loop (WriteMsgUDP) AND the receive routines
# (ReadMsgUDP) -- the v4/v6 receive workers were dying on startup with
# "wsarecvmsg ... invalid pointer address", so the server's handshake RESPONSE
# could never be read and the tunnel never completed (windows-brat r4 live test).
# Linux keeps make([]byte, controlSize) (controlSize>0). The receive path
# re-slices OOB[:cap()] -> nil stays nil; setSrcControl is a no-op on Windows.
$allocOld = 'msgs[i].OOB = make([]byte, controlSize)'
$allocNew = 'if controlSize > 0 { msgs[i].OOB = make([]byte, controlSize) }'
# (2) Belt-and-suspenders on the send call site (cheap; guards any future
# non-nil empty OOB reaching WriteMsgUDP regardless of the pool state).
# (2b) v2.45.0-r11: RETRY-ON-WSAENOBUFS. Under a UDP burst (Steam Datagram
# Relay pings ~330 flows in a moment, all multiplexed into this single physical
# AWG socket) WriteMsgUDP returns WSAENOBUFS (10055) "queue was full"; upstream
# wireguard-go then DROPS the whole batch with no retry (device/send.go) -> the
# game's outbound pings never leave -> Dota "all regions ОШИБКА" (live-confirmed
# 2026-07-02, config=OK anyrelay=Failed). WSAENOBUFS is transient (the send
# buffer drains in µs as the NIC transmits), so retry the same datagram a few
# times with a tiny backoff instead of dropping. errors+syscall already imported;
# time added below. Full RCA: plans/sdr-research-realtime-games-nat-2026-07-02.md.
$timeOld = "`t`"syscall`""
$timeNew = "`t`"syscall`"`n`t`"time`""
$sendOld = '_, _, err = conn.WriteMsgUDP(msg.Buffers[0], msg.OOB, msg.Addr.(*net.UDPAddr))'
$sendNew = 'oob := msg.OOB; if len(oob) == 0 { oob = nil }; for _rn := 0; ; _rn++ { _, _, err = conn.WriteMsgUDP(msg.Buffers[0], oob, msg.Addr.(*net.UDPAddr)); if err == nil || _rn >= 8 || !errors.Is(err, syscall.Errno(10055)) { break }; time.Sleep(time.Duration(80*(_rn+1)) * time.Microsecond) }'
# (3) AWG H4 transport-header clobber. The Leadaxe fork kept sagernet's
# Cloudflare-WARP "reserved bytes" feature: receiveIP UNCONDITIONALLY zeroes
# bytes [1:4] of every inbound datagram BEFORE classification. AmneziaWG
# repurposes those bytes as the uint32 transport magic header H4 (at offset s4;
# s4=0 here so H4 sits at byte 0). So a received transport packet H4=1028816851
# (LE d3 7f 52 3d) becomes d3 00 00 00 = 211, fails headers.transport.Validate,
# and is logged "received message with unknown type" -> no data flows, the
# session re-handshakes (windows-brat r5 live test: handshake OK, keepaliveRecv=0,
# 8x unknown type). The SEND side is already gated `if loaded` (reservedForEndpoint
# is never set for AWG), so only the receive clear corrupts. Fix: gate the receive
# clear the same way -- only zero reserved bytes when a reserved value is actually
# configured for that peer (WARP), which is NEVER for AWG. Canonical
# amnezia-vpn/amneziawg-go has no such clear. Matches the diagnosis workflow
# (wf_5a9efb1a) byte-math + ref-bind diff.
$clearOld = 'common.ClearArray(bufs[i][1:4])'
$clearNew = 'if _, resvLoaded := s.reservedForEndpoint[M.AddrPortFromNet(msg.Addr)]; resvLoaded { common.ClearArray(bufs[i][1:4]) }'

foreach ($pair in @(
    @{ o = $timeOld;  n = $timeNew;  name = 'time import (ENOBUFS backoff)' },
    @{ o = $allocOld; n = $allocNew; name = 'pooled-OOB allocation (send+recv root fix)' },
    @{ o = $sendOld;  n = $sendNew;  name = 'WriteMsgUDP send site + WSAENOBUFS retry' },
    @{ o = $clearOld; n = $clearNew; name = 'reserved-byte receive clear (AWG H4 clobber)' })) {
    $cnt = ([regex]::Matches($bindSrc, [regex]::Escape($pair.o))).Count
    if ($cnt -ne 1) {
        throw "FATAL: expected exactly 1 '$($pair.name)' in conn/bind_std.go, found $cnt. The fork source changed -- re-vet the AWG-on-Windows patches (golang/go#77875 + reserved-byte H4 clobber) before building."
    }
    $bindSrc = $bindSrc.Replace($pair.o, $pair.n)
}
[System.IO.File]::WriteAllText($bindStd, $bindSrc, (New-Object System.Text.UTF8Encoding $false))
$bindChk = Get-Content -Raw $bindStd
if (($bindChk -notmatch [regex]::Escape($allocNew)) -or ($bindChk -notmatch [regex]::Escape($sendNew)) -or ($bindChk -notmatch [regex]::Escape($clearNew)) -or ($bindChk -notmatch [regex]::Escape($timeNew))) {
    throw "FATAL: conn/bind_std.go AWG-on-Windows patch did not apply."
}
Write-Host "Patched: empty-OOB nil-guard send+recv (golang/go#77875) + AWG H4 reserved-byte receive-clear gate + WSAENOBUFS send-retry." -ForegroundColor Green

# Wintun's deterministic RequestedGUID path can create a half-visible adapter,
# wait 15 seconds, then fail with ERROR_ALREADY_EXISTS and remove it before
# sing-tun's OpenAdapter fallback runs. Vendor after the AWG source patches so
# both fixes are compiled from the same materialized dependency tree.
Write-Host "[2.75/4] Patch sing-tun Wintun RequestedGUID" -ForegroundColor Yellow
& go -C $src mod vendor
if ($LASTEXITCODE -ne 0) { throw "go mod vendor failed ($LASTEXITCODE)" }
$vendorBindStd = Join-Path $src 'vendor\github.com\sagernet\wireguard-go\conn\bind_std.go'
if (-not (Test-Path $vendorBindStd)) { throw "FATAL: vendored patched wireguard-go source not found." }
$vendorBindChk = Get-Content -Raw $vendorBindStd
if (($vendorBindChk -notmatch [regex]::Escape($allocNew)) -or ($vendorBindChk -notmatch [regex]::Escape($sendNew)) -or ($vendorBindChk -notmatch [regex]::Escape($clearNew)) -or ($vendorBindChk -notmatch [regex]::Escape($timeNew))) {
    throw "FATAL: go mod vendor did not preserve the patched AWG wireguard-go source."
}
$tunWindows = Join-Path $src 'vendor\github.com\sagernet\sing-tun\tun_windows.go'
if (-not (Test-Path $tunWindows)) { throw "FATAL: $tunWindows not found; re-vet the Wintun RequestedGUID patch." }
$tunSrc = Get-Content -Raw $tunWindows
$tunCreateOld = 'wintun.CreateAdapter(options.Name, TunnelType, generateGUIDByDeviceName(options.Name))'
$tunCreateNew = 'wintun.CreateAdapter(options.Name, TunnelType, nil)'
if (([regex]::Matches($tunSrc, [regex]::Escape($tunCreateOld))).Count -ne 1) {
    throw "FATAL: expected exactly one deterministic Wintun CreateAdapter call; fork source changed."
}
$tunSrc = $tunSrc.Replace($tunCreateOld, $tunCreateNew)
[System.IO.File]::WriteAllText($tunWindows, $tunSrc, (New-Object System.Text.UTF8Encoding $false))
if (([regex]::Matches((Get-Content -Raw $tunWindows), [regex]::Escape($tunCreateNew))).Count -ne 1) {
    throw "FATAL: Wintun RequestedGUID patch did not apply exactly once."
}

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
# build tags and is NOT forgeable -- assert with_awg + with_xhttp are actually
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
# base wireguard endpoint. Write UTF-8 WITHOUT BOM -- PS5.1 `Set-Content -Encoding utf8`
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
    if ($LASTEXITCODE -ne 0) { throw "FATAL: sing-box-lx rejected a minimal AWG endpoint config (exit $LASTEXITCODE) -- with_awg not functional." }
    Write-Host "Verified: with_awg + with_xhttp present; AWG endpoint config accepted." -ForegroundColor Green
} finally { Remove-Item -Force $awgProbe -ErrorAction SilentlyContinue }

# Runtime handshake-send smoke — catches the golang/go#77875 WSAEFAULT regression
# that `check` CANNOT (check is static; it never exercises the Windows UDP send
# path). This actually RUNS the AWG endpoint against a black-hole peer for a few
# seconds and asserts the handshake initiation SENDS. Pre-patch (or built on an
# affected Go without the bind_std.go fix) the log carries
# "failed to send handshake initiation: ... wsasendmsg ... invalid pointer
# address" and the tunnel can never establish on Windows (the bug that silently
# broke v2.45.0-r1..r3). Post-patch it logs "sending handshake initiation" +
# "Handshake did not complete after 5 seconds" (send OK, black-hole peer mute).
Write-Host "[4.5/4] Runtime handshake-send smoke (golang/go#77875)" -ForegroundColor Yellow
$hsLog = Join-Path ([System.IO.Path]::GetTempPath()) "awg-hssmoke-$PID.log"
$hsCfg = Join-Path ([System.IO.Path]::GetTempPath()) "awg-hssmoke-$PID.json"
$hsLogFwd = $hsLog.Replace('\', '/')
$hsJson = @"
{ "log": { "level": "debug", "output": "$hsLogFwd" },
  "inbounds": [ { "type": "socks", "listen": "127.0.0.1", "listen_port": 21766 } ],
  "endpoints": [ { "type":"wireguard","tag":"proxy","system":false,"mtu":1280,"address":["10.66.0.2/32"],
    "private_key":"aGVsbG8taGVsbG8taGVsbG8taGVsbG8taGVsbG8tMDA=","jc":4,"jmin":40,"jmax":70,"s1":50,"s2":50,
    "peers":[{"address":"192.0.2.1","port":51820,"public_key":"aGVsbG8taGVsbG8taGVsbG8taGVsbG8taGVsbG8tMDA=","allowed_ips":["0.0.0.0/0"],"persistent_keepalive_interval":25}] } ],
  "outbounds": [ { "type":"direct","tag":"direct" } ],
  "route": { "final":"proxy" } }
"@
[System.IO.File]::WriteAllText($hsCfg, $hsJson, (New-Object System.Text.UTF8Encoding $false))
$hsProc = Start-Process -FilePath $OutputPath -ArgumentList @('run', '-c', $hsCfg) -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
try { Invoke-WebRequest -Uri 'http://192.0.2.9/' -Proxy 'socks5://127.0.0.1:21766' -TimeoutSec 2 -UseBasicParsing | Out-Null } catch { }
Start-Sleep -Seconds 7
try { $hsProc.Kill() } catch { }
Start-Sleep -Milliseconds 500
$hsOut = if (Test-Path $hsLog) { Get-Content $hsLog -Raw } else { '' }
Remove-Item -Force $hsCfg, $hsLog -ErrorAction SilentlyContinue
if ($hsOut -match 'wsasendmsg' -or $hsOut -match 'wsarecvmsg') {
    throw "FATAL: AWG hit WSASendMsg/WSARecvMsg WSAEFAULT -- the golang/go#77875 bind_std.go patch is NOT effective (send OR receive path). Do NOT bundle this binary (this is the bug that broke AWG on Windows in v2.45.0-r1..r4). The receive-path failure silently stops the handshake RESPONSE from being read."
}
if ($hsOut -match 'andshake did not complete' -or $hsOut -match 'ending handshake initiation') {
    Write-Host "Verified: AWG handshake SENDS + receive routines stay up, no WSAEFAULT (send+recv patch effective)." -ForegroundColor Green
} else {
    Write-Host "WARN: handshake smoke saw no WSAEFAULT, but also no send attempt logged -- inspect manually before trusting." -ForegroundColor Yellow
}
Write-Host "Built: $OutputPath" -ForegroundColor Green
Write-Host "Bundle it:  powershell -File build.ps1 -Version <X.Y.Z-rN> -SingBoxPath `"$OutputPath`" -Upload" -ForegroundColor Cyan
