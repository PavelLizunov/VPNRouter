param(
    [string]$OutputPath = "$PSScriptRoot\..\publish\sing-box-lx.exe",
    [string]$WorkDir = (Join-Path $env:TEMP "vpnrouter-singbox-lx")
)
$ErrorActionPreference = 'Stop'

$LX_REPO   = 'https://github.com/Leadaxe/sing-box-lx'
$LX_COMMIT = 'c7a2592e750406ade9ebaae1d0fdb7482fc0773e'
$WG_REPO   = 'https://github.com/Leadaxe/wireguard-go-awg2-lx'
$WG_BRANCH = 'lx'
$WG_COMMIT = '0c0c10b5d3236796bd3832a6813223d6dc7d0bb1'
$UPSTREAM_REPO = 'https://github.com/SagerNet/sing-box.git'
$TUN_BACKPORT  = '0b7ffbaafa5f060dd8c762dfbc751d592cba1fea'  # F1: sing-tun v0.8.11 (TUN system-stack TCP NAT collision)
$DNS_BACKPORT  = '72a8723e13b9574664f4c78e588069fa4aca6fc9'  # F2: DNS nested single-flight self-deadlock
$TAGS = 'with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_clash_api,with_naive_outbound,with_purego,badlinkname,tfogo_checklinkname0,with_xhttp,with_awg'
$VER  = '1.13.13-lx-awg'

if (-not (Get-Command go -ErrorAction SilentlyContinue)) { throw "Go toolchain not found on PATH." }

function Invoke-Git { param([string[]]$GitArgs)
    & git @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed ($LASTEXITCODE)" }
}

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

Write-Host "[1.5/4] Backport upstream fixes (sing-tun NAT + DNS single-flight)" -ForegroundColor Yellow
Invoke-Git @('-C', $src, 'fetch', '--quiet', $UPSTREAM_REPO, $TUN_BACKPORT, $DNS_BACKPORT)
Invoke-Git @('-C', $src, 'cherry-pick', '--no-commit', $TUN_BACKPORT, $DNS_BACKPORT)

$goMod = Get-Content -Raw (Join-Path $src 'go.mod')
if (-not $goMod.Contains('github.com/sagernet/sing-tun v0.8.11')) {
    throw "FATAL: go.mod missing 'github.com/sagernet/sing-tun v0.8.11' after TUN backport ($TUN_BACKPORT). The sing-tun bump did not apply; refusing to build an unpatched core."
}
if ($goMod.Contains('github.com/sagernet/sing-tun v0.8.10')) {
    throw "FATAL: go.mod still pins 'github.com/sagernet/sing-tun v0.8.10' after TUN backport ($TUN_BACKPORT). The TUN TCP NAT collision fix is NOT in this tree; refusing to build."
}
$dnsClient = Get-Content -Raw (Join-Path $src 'dns\client.go')
if (-not $dnsClient.Contains('compatible.Map[transportCacheKey, chan struct{}]')) {
    throw "FATAL: dns/client.go missing 'compatible.Map[transportCacheKey, chan struct{}]' after DNS backport ($DNS_BACKPORT). The single-flight deadlock fix did not apply; refusing to build."
}
if (-not $dnsClient.Contains('cacheKey := transportCacheKey{Question: question, transportTag: transport.Tag()}')) {
    throw "FATAL: dns/client.go missing the transportCacheKey cache-key construction after DNS backport ($DNS_BACKPORT). The single-flight deadlock fix did not apply; refusing to build."
}
if ($dnsClient.Contains('compatible.Map[dns.Question, chan struct{}]')) {
    throw "FATAL: dns/client.go still uses 'compatible.Map[dns.Question, chan struct{}]' after DNS backport ($DNS_BACKPORT). The pre-fix single-flight map is still present; refusing to build."
}
Write-Host "Backported: sing-tun v0.8.11 (TUN NAT) + DNS single-flight deadlock fix; fail-closed assertions passed." -ForegroundColor Green

Write-Host "[2/4] Clone wireguard-go-awg2-lx @ $WG_COMMIT (submodule path)" -ForegroundColor Yellow
$wg = Join-Path $src 'submodules\wireguard-go'
if (Test-Path $wg) { Remove-Item -Recurse -Force $wg }
Invoke-Git @('clone', '--quiet', '-b', $WG_BRANCH, $WG_REPO, $wg)
Invoke-Git @('-C', $wg, 'checkout', '--quiet', $WG_COMMIT)
Assert-GitHead -RepoDir $wg -Expected $WG_COMMIT -Label 'wireguard-go-awg2-lx'

Write-Host "[2.5/4] Patch conn/bind_std.go (golang/go#77875 WSAEFAULT, send+recv)" -ForegroundColor Yellow
$bindStd = Join-Path $wg 'conn\bind_std.go'
if (-not (Test-Path $bindStd)) { throw "FATAL: $bindStd not found (fork layout changed); re-vet the WSAEFAULT patch." }
$bindSrc = Get-Content -Raw $bindStd

$allocOld = 'msgs[i].OOB = make([]byte, controlSize)'
$allocNew = 'if controlSize > 0 { msgs[i].OOB = make([]byte, controlSize) }'
$timeOld = "`t`"syscall`""
$timeNew = "`t`"syscall`"`n`t`"time`""
$sendOld = '_, _, err = conn.WriteMsgUDP(msg.Buffers[0], msg.OOB, msg.Addr.(*net.UDPAddr))'
$sendNew = 'oob := msg.OOB; if len(oob) == 0 { oob = nil }; for _rn := 0; ; _rn++ { _, _, err = conn.WriteMsgUDP(msg.Buffers[0], oob, msg.Addr.(*net.UDPAddr)); if err == nil || _rn >= 8 || !errors.Is(err, syscall.Errno(10055)) { break }; time.Sleep(time.Duration(80*(_rn+1)) * time.Microsecond) }'
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
