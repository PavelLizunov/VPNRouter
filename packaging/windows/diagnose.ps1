# VPNRouter YouTube diagnostic (FULL-TUNNEL configs only)
# One-liner:  iwr -useb https://vpn.ninitux.com/diagnose.ps1 | iex
#
# Run this WHILE the VPN is connected on a *full-tunnel* config. It walks the
# YouTube path layer by layer (TUN -> proxy -> DNS -> TCP -> QUIC -> HTTP ->
# egress -> live conns) and prints OK/FAIL per layer + a best-guess verdict.
# Read-only: it never changes settings and never uploads anything - it saves a
# text report you send back. Secrets (uuid / keys / sub-token) are not printed.

$ErrorActionPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$pd       = $env:ProgramData
$cfgYaml  = Join-Path $pd 'VPNRouter\config.yaml'
$curJson  = Join-Path $pd 'VPNRouter\config\current.json'
$stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile  = Join-Path $pd ("VPNRouter\logs\youtube-diag-$stamp.txt")
$report   = New-Object System.Collections.Generic.List[string]

function Line($s) { Write-Host $s; $report.Add($s) | Out-Null }
function Sec($s)  { Line ''; Line ('== ' + $s + ' ==') }
function Verdict($layer, $ok, $detail) {
    $tag = if ($ok -eq $true) { '[ OK ]' } elseif ($ok -eq $false) { '[FAIL]' } else { '[ ?? ]' }
    $col = if ($ok -eq $true) { 'Green' } elseif ($ok -eq $false) { 'Red' } else { 'Yellow' }
    Write-Host ("{0} {1,-14} {2}" -f $tag, $layer, $detail) -ForegroundColor $col
    $report.Add(("{0} {1,-14} {2}" -f $tag, $layer, $detail)) | Out-Null
}
function Timed($block) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $r = & $block; $sw.Stop()
    [pscustomobject]@{ Result = $r; Ms = [int]$sw.ElapsedMilliseconds }
}

Line ("VPNRouter YouTube diagnostic  ($stamp)")
$ver = (Get-Item 'C:\Program Files\VPNRouter\app\VPNRouter.App.exe' -EA SilentlyContinue).VersionInfo.ProductVersion
if ($ver) { Line ("app build: $ver") }

# ---------- Layer 0: config ----------
Sec 'Config'
$routingMode = 'unknown'; $strictDns = $false; $lockdown = $false; $blockQuic = $true
if (Test-Path $cfgYaml) {
    foreach ($l in Get-Content $cfgYaml) {
        if ($l -match '^\s*routing_mode\s*:\s*(\S+)')          { $routingMode = $matches[1].Trim('"').Trim("'") }
        if ($l -match '^\s*strict_dns\s*:\s*(true|false)')      { $strictDns = ($matches[1] -eq 'true') }
        if ($l -match '^\s*dns_leak_lockdown\s*:\s*(true|false)') { $lockdown = ($matches[1] -eq 'true') }
        if ($l -match '^\s*block_quic_on_tcp_proxy\s*:\s*(true|false)') { $blockQuic = ($matches[1] -eq 'true') }
    }
}
Line ("routing_mode = $routingMode | strict_dns=$strictDns | dns_leak_lockdown=$lockdown | block_quic_on_tcp_proxy=$blockQuic")
if ($routingMode -ne 'full') {
    Verdict 'config' $null "routing_mode is '$routingMode', not 'full' - this test is built for full-tunnel; results may be partial."
}

# current.json: proxy type, dns.final, quic-reject rule, clash api addr
$clashAddr = '127.0.0.1:9090'; $proxyTypes = @(); $dnsFinal = '?'; $quicReject = $false; $hasUdpProxy = $false
if (Test-Path $curJson) {
    try {
        $cj = Get-Content $curJson -Raw | ConvertFrom-Json
        if ($cj.experimental.clash_api.external_controller) { $clashAddr = $cj.experimental.clash_api.external_controller }
        if ($cj.dns.'final') { $dnsFinal = $cj.dns.'final' }
        foreach ($o in $cj.outbounds) {
            if ($o.type -and $o.type -notin @('direct','block','dns','selector','urltest')) { $proxyTypes += $o.type }
        }
        $proxyTypes = $proxyTypes | Select-Object -Unique
        if ($proxyTypes -contains 'hysteria2' -or $proxyTypes -contains 'tuic') { $hasUdpProxy = $true }
        foreach ($r in $cj.route.rules) { if ($r.protocol -eq 'quic' -and $r.action -eq 'reject') { $quicReject = $true } }
    } catch { Line ("could not parse current.json: " + $_.Exception.Message) }
}
Line ("proxy outbound(s) = " + ($(if ($proxyTypes) { $proxyTypes -join ',' } else { 'none?' })) + " | dns.final=$dnsFinal | quic-reject-rule=$quicReject")
$clashBase = "http://$clashAddr"

# ---------- Layer 1: VPN / TUN ----------
Sec 'VPN / TUN'
$sb = Get-Process -Name 'sing-box' -EA SilentlyContinue
Verdict 'sing-box' ([bool]$sb) ($(if ($sb) { "running (PID $($sb.Id))" } else { 'NOT running - connect the VPN first, then re-run' }))
$tun = Get-NetAdapter -EA SilentlyContinue | Where-Object { $_.InterfaceDescription -match 'WireGuard|TUN|sing-box' -or $_.Name -match 'VPNRouter|sing-box|tun' }
Verdict 'TUN adapter' ([bool]$tun) ($(if ($tun) { ($tun.Name -join ', ') } else { 'no TUN adapter found' }))
$clashOk = $false
try { $v = Invoke-RestMethod "$clashBase/version" -TimeoutSec 4; $clashOk = [bool]$v.version; Verdict 'Clash API' $true ("serving (sing-box $($v.version))") }
catch { Verdict 'Clash API' $false "$clashBase not responding" }
if (-not $sb) { Line ''; Line 'VPN is not running - stop here, connect, and re-run.'; $report -join "`r`n" | Out-File $outFile -Encoding utf8; Line "saved: $outFile"; return }

# ---------- Layer 2: proxy reachability ----------
Sec 'Proxy reachability'
$delay = $null
try {
    $u = [uri]::EscapeDataString('http://www.gstatic.com/generate_204')
    $d = Invoke-RestMethod "$clashBase/proxies/proxy/delay?timeout=5000&url=$u" -TimeoutSec 8
    $delay = $d.delay
} catch {}
Verdict 'proxy delay' ([bool]$delay) ($(if ($delay) { "$delay ms (server reachable through the tunnel)" } else { 'proxy could NOT reach the test URL - the selected server is dead/slow' }))

# ---------- Layer 3: DNS ----------
Sec 'DNS (resolves through the tunnel in full-tunnel)'
$ytIp = $null; $gvIp = $null
foreach ($h in 'www.youtube.com','youtube.com','rr1---sn-4g5e6nzz.googlevideo.com') {
    $t = Timed { Resolve-DnsName -Name $h -Type A -EA SilentlyContinue | Where-Object { $_.IPAddress } | Select-Object -First 1 -Expand IPAddress }
    $ok = [bool]$t.Result
    if ($h -match 'youtube' -and -not $ytIp) { $ytIp = $t.Result }
    if ($h -match 'googlevideo' -and -not $gvIp) { $gvIp = $t.Result }
    Verdict "dns $h" $ok ($(if ($ok) { "$($t.Result)  ($($t.Ms) ms)" } else { "no answer ($($t.Ms) ms) - DNS via tunnel failing" }))
}

# ---------- Layer 4: TCP 443 ----------
Sec 'TCP 443'
foreach ($pair in @(@('youtube', $ytIp), @('googlevideo', $gvIp))) {
    $nm = $pair[0]; $ip = $pair[1]
    if (-not $ip) { Verdict "tcp $nm" $null 'skipped (no IP from DNS)'; continue }
    $t = Timed { Test-NetConnection -ComputerName $ip -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue }
    Verdict "tcp $nm" ([bool]$t.Result) ("$ip`:443  ($($t.Ms) ms)")
}

# ---------- Layer 5: QUIC / HTTP-3 (the classic 'endless loading') ----------
Sec 'QUIC / HTTP-3'
if ($hasUdpProxy) {
    Verdict 'quic' $true "proxy carries UDP (hysteria2/tuic) - QUIC works natively, no block needed"
} elseif ($quicReject) {
    Verdict 'quic' $true "TCP-only proxy + QUIC reject rule present - browser falls back to TCP (correct)"
} else {
    Verdict 'quic' $false ("TCP-only proxy ($($proxyTypes -join ',')) and NO QUIC reject rule - YouTube QUIC will hang ('endless loading'). " +
        $(if ($blockQuic) { "'Block QUIC' is ON in settings but the rule is missing from current.json - reconnect to regenerate." } else { "Turn ON 'Block QUIC on TCP-only proxy' in Leak protection." }))
}

# ---------- Layer 6: HTTP round-trip ----------
# Any HTTP status back = the server answered = reachability OK (even a 404).
# Only a timeout / no-response (null) is a real FAIL. Works on PS 5.1 (throws
# System.Net.WebException) and PS 7 (HttpResponseException) - both expose
# .Exception.Response.StatusCode.
Sec 'HTTP round-trip (through the tunnel)'
foreach ($url in 'http://www.gstatic.com/generate_204','https://www.youtube.com','https://www.youtube.com/generate_204') {
    $t = Timed {
        try { [int](Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 20 -MaximumRedirection 2).StatusCode }
        catch {
            $resp = $_.Exception.Response
            if ($resp -and $resp.StatusCode) { [int]$resp.StatusCode } else { $null }
        }
    }
    $code = $t.Result
    $ok = ($code -ne $null)
    $lbl = ($url -replace '^https?://','' )
    Verdict "http $lbl" $ok ($(if ($ok) { "HTTP $code  ($($t.Ms) ms)" } else { "no response / timeout ($($t.Ms) ms)" }))
}

# ---------- Layer 7: egress IP ----------
Sec 'Egress IP (should be the VPN server, not your ISP)'
$eip = $null
foreach ($svc in 'https://api.ipify.org','https://ifconfig.me/ip') { if (-not $eip) { try { $eip = (Invoke-WebRequest $svc -UseBasicParsing -TimeoutSec 10).Content.Trim() } catch {} } }
Verdict 'egress ip' ([bool]$eip) ($(if ($eip) { $eip } else { 'could not determine - outbound HTTP is failing' }))

# ---------- Layer 8: live connections ----------
Sec 'Live connections (Clash)'
try {
    $conns = (Invoke-RestMethod "$clashBase/connections" -TimeoutSec 6).connections
    $yt = $conns | Where-Object { $_.metadata.host -match 'youtube|googlevideo|ytimg|ggpht' }
    if ($yt) {
        Line ("active YouTube-related connections: " + ($yt | Measure-Object).Count)
        $yt | Select-Object -First 8 | ForEach-Object {
            Line ("  {0}:{1}  -> chains=[{2}]  up={3} dn={4}" -f $_.metadata.host, $_.metadata.destinationPort, ($_.chains -join '>'), $_.upload, $_.download)
        }
    } else { Line 'no active youtube/googlevideo connections right now (open a video, then re-run while it spins)' }
} catch { Line "couldn't read /connections" }

# ---------- Verdict ----------
Sec 'BEST GUESS'
if (-not $delay) {
    Line 'Server is unreachable through the tunnel -> the selected server is dead/slow. Switch servers and retest.'
} elseif (-not $hasUdpProxy -and -not $quicReject) {
    Line "QUIC is NOT blocked on a TCP-only proxy -> classic YouTube 'endless loading'. Enable 'Block QUIC on TCP-only proxy' (or reconnect to regenerate the rule)."
} elseif ($ytIp -and -not $gvIp) {
    Line 'youtube.com resolves but googlevideo (video CDN) does not -> DNS issue specifically on the CDN domain.'
} else {
    Line 'No single obvious break above. Send this report - the per-layer timings tell us where it stalls.'
}

Line ''
$report -join "`r`n" | Out-File $outFile -Encoding utf8
Line "Report saved: $outFile"
Line 'Send me the contents of that file (or this console output).'
