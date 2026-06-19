<#
  vpn-diag.ps1 — VPNRouter network diagnostic suite (Windows).
  PowerShell twin of tools/vpn-diag.sh. Run once with the VPN OFF (baseline) and
  once with it ON, then compare — that's how we quantify what the tunnel changes
  (e.g. the full-tunnel ChatGPT failure). Uses the bundled curl.exe (Win10/11) for
  HTTP/throughput so the -w metrics match the bash version.

  Usage:  powershell -ExecutionPolicy Bypass -File vpn-diag.ps1 baseline
          powershell -ExecutionPolicy Bypass -File vpn-diag.ps1 vpn-full
#>
param([string]$Label = "run")
$ErrorActionPreference = "SilentlyContinue"
$ts  = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path (Get-Location) "vpn-diag-$Label-$ts.txt"
$curl = "$env:SystemRoot\System32\curl.exe"
function Log([string]$m){ $m | Tee-Object -FilePath $out -Append }
function HR(){ Log "------------------------------------------------------------" }

$HTTP = @("https://chatgpt.com/","https://chat.openai.com/","https://api.anthropic.com/","https://www.youtube.com/","https://www.google.com/","https://api.telegram.org/","https://github.com/")
$DNS  = @("chatgpt.com","api.anthropic.com","www.youtube.com","api.telegram.org","www.google.com")
$PING = @("1.1.1.1","8.8.8.8")
$TCP  = @("chatgpt.com","api.anthropic.com","www.youtube.com")
$MTUTARGET = "1.1.1.1"

Log "============================================================"
Log " VPNRouter diagnostic - label='$Label'  $(Get-Date -Format o)"
Log " host=$env:COMPUTERNAME  os=$([System.Environment]::OSVersion.VersionString)"
Log "============================================================"

# 1) Egress IP + geo
HR; Log "[1] Egress IP + geolocation"
$geo = & $curl -s -m 8 "https://ipinfo.io/json" | ConvertFrom-Json
if ($geo.ip) { Log "    IP=$($geo.ip)  $($geo.city)/$($geo.country)  $($geo.org)" }
else { Log "    FAILED to reach ipinfo.io (no egress / DNS down)" }

# 2) DNS resolution (timed)
HR; Log "[2] DNS resolution (timed)"
foreach ($d in $DNS) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $a = [System.Net.Dns]::GetHostAddresses($d) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
  $sw.Stop()
  if ($a) { Log ("    {0,-22} {1}  ({2} ms)" -f $d, $a.IPAddressToString, $sw.ElapsedMilliseconds) }
  else    { Log ("    {0,-22} RESOLVE FAILED" -f $d) }
}

# 3) ICMP ping (RTT + loss)
HR; Log "[3] ICMP ping (10 pkts: avg RTT + loss)"
foreach ($p in $PING) {
  $o = ping.exe -n 10 $p 2>$null
  $mLoss = ($o | Select-String "Lost = (\d+)")  ; $loss = if ($mLoss) { $mLoss.Matches.Groups[1].Value } else { "?" }
  $mAvg  = ($o | Select-String "Average = (\d+)ms"); $avg  = if ($mAvg)  { $mAvg.Matches.Groups[1].Value }  else { "?" }
  Log ("    {0,-10} lost={1}/10  avg={2} ms" -f $p, $loss, $avg)
}

# 4) TCP+TLS connect latency :443
HR; Log "[4] TCP+TLS connect latency :443 (3 tries each)"
foreach ($h in $TCP) {
  $best = $null
  for ($i=0; $i -lt 3; $i++) {
    $r = & $curl -s -m 8 -o NUL -w "%{time_connect}|%{time_appconnect}" "https://$h" 2>$null
    if ($r) { $best = $r; break }
  }
  if ($best) {
    $tcp = [double]($best.Split("|")[0]); $tls = [double]($best.Split("|")[1])
    Log ("    {0,-22} tcp={1}ms tls={2}ms" -f "$h`:443", [math]::Round($tcp*1000), [math]::Round($tls*1000))
  } else { Log ("    {0,-22} CONNECT FAILED" -f "$h`:443") }
}

# 5) HTTP reachability (the key check)
HR; Log "[5] HTTP reachability - status + TTFB + total + bytes"
foreach ($u in $HTTP) {
  $r = & $curl -s -A "Mozilla/5.0 vpn-diag" -m 20 -o NUL -w "%{http_code}|%{time_starttransfer}|%{time_total}|%{size_download}" $u 2>$null
  if ($r) {
    $p = $r.Split("|"); $code=$p[0]; $ttfb=[double]$p[1]; $tot=[double]$p[2]; $sz=$p[3]
    $verdict = "ok"
    if ($code -eq "000") { $verdict = "**UNREACHABLE**" }
    elseif ([int]$code -ge 400) { $verdict = "http-$code" }
    Log ("    {0,-30} code={1} ttfb={2}ms total={3}ms bytes={4} {5}" -f $u,$code,[math]::Round($ttfb*1000),[math]::Round($tot*1000),$sz,$verdict)
  } else { Log ("    {0,-30} **UNREACHABLE (curl failed)**" -f $u) }
}

# 6) Throughput (download)
HR; Log "[6] Throughput (download)"
$dn = $false
foreach ($src in @("https://cachefly.cachefly.net/10mb.test","https://speed.cloudflare.com/__down?bytes=25000000")) {
  $r = & $curl -s -m 40 -o NUL -w "%{speed_download}|%{size_download}|%{http_code}" $src 2>$null
  if ($r) {
    $p=$r.Split("|"); $bps=[double]$p[0]; $sz=[long]$p[1]; $code=$p[2]
    if ($sz -gt 1000000) {
      Log ("    download: {0} Mbit/s  ({1} MiB/s, {2} bytes via {3})" -f [math]::Round($bps/125000,1),[math]::Round($bps/1048576,1),$sz,($src.Split('/')[-1]))
      $dn = $true; break
    }
  }
}
if (-not $dn) { Log "    download: FAILED/throttled on all sources" }

# 7) Path-MTU probe (DF flag)
HR; Log "[7] Path-MTU probe (largest unfragmented to $MTUTARGET)"
$found = $false
foreach ($payload in 1472,1464,1422,1392,1352,1252) {
  $o = ping.exe -n 1 -w 2000 -f -l $payload $MTUTARGET 2>$null
  if ($o | Select-String "bytes=") {
    Log ("    OK at payload {0} -> path MTU >= {1}" -f $payload, ($payload+28)); $found = $true; break
  } elseif ($o | Select-String "fragmented") {
    Log ("    frag needed at payload {0} (MTU {1})" -f $payload, ($payload+28))
  } else {
    Log ("    no reply at payload {0} (timeout/blocked)" -f $payload)
  }
}
if (-not $found) { Log "    (no size succeeded - ICMP PMTU may be blocked on this path)" }

HR; Log "Report saved: $out"
Log "Tip: run with VPN off ('baseline') then on ('vpn-full') and compare."
