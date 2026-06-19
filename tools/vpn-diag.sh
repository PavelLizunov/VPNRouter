#!/usr/bin/env bash
# vpn-diag.sh — VPNRouter network diagnostic suite (Linux/macOS).
#
# Runs a battery of connectivity/latency/throughput/reachability checks and prints
# a readable report. Run it once WITHOUT the VPN (baseline) and once WITH the VPN
# connected, then diff the two — that's how we quantify what the tunnel changes
# (e.g. the full-tunnel ChatGPT failure). Degrades gracefully when a tool is absent.
#
# Usage:  ./vpn-diag.sh [label]      # label tags the report, e.g. "baseline" / "vpn-full"
# Output: human report to stdout; machine summary to ./vpn-diag-<label>-<ts>.txt
set -u

LABEL="${1:-run}"
TS="$(date +%Y%m%d-%H%M%S)"
OUT="vpn-diag-${LABEL}-${TS}.txt"

# Targets the user cares about (the ChatGPT-in-full-tunnel case + general).
HTTP_TARGETS=(
  "https://chatgpt.com/"
  "https://chat.openai.com/"
  "https://api.anthropic.com/"
  "https://www.youtube.com/"
  "https://www.google.com/"
  "https://api.telegram.org/"
  "https://github.com/"
)
DNS_TARGETS=(chatgpt.com api.anthropic.com www.youtube.com api.telegram.org www.google.com)
PING_TARGETS=(1.1.1.1 8.8.8.8)
TCP_TARGETS=("chatgpt.com:443" "api.anthropic.com:443" "www.youtube.com:443")
MTU_TARGET="1.1.1.1"

log() { echo "$@" | tee -a "$OUT"; }
hr()  { log "------------------------------------------------------------"; }
have(){ command -v "$1" >/dev/null 2>&1; }

log "============================================================"
log " VPNRouter diagnostic — label='${LABEL}'  $(date -Is)"
log " host=$(hostname)  os=$(uname -sr)"
log "============================================================"

# 1) Egress IP + geo -------------------------------------------------------------
hr; log "[1] Egress IP + geolocation"
geo="$(curl -s -m 8 https://ipinfo.io/json 2>/dev/null)"
if [ -n "$geo" ]; then
  ip=$(echo "$geo" | grep -oE '"ip"[^,]*' | cut -d'"' -f4)
  city=$(echo "$geo" | grep -oE '"city"[^,]*' | cut -d'"' -f4)
  country=$(echo "$geo" | grep -oE '"country"[^,]*' | cut -d'"' -f4)
  org=$(echo "$geo" | grep -oE '"org"[^,]*' | cut -d'"' -f4)
  log "    IP=${ip}  ${city}/${country}  ${org}"
else
  log "    FAILED to reach ipinfo.io (no egress / DNS down)"
fi

# 2) DNS resolution (timed) ------------------------------------------------------
hr; log "[2] DNS resolution (timed)"
for d in "${DNS_TARGETS[@]}"; do
  t0=$(date +%s%3N)
  if have getent; then addr=$(getent ahostsv4 "$d" 2>/dev/null | awk 'NR==1{print $1}')
  else addr=$(curl -s -m6 -o /dev/null -w '%{remote_ip}' "https://$d" 2>/dev/null); fi
  t1=$(date +%s%3N)
  if [ -n "${addr:-}" ]; then log "    $(printf '%-22s' "$d") ${addr}  ($((t1-t0)) ms)"
  else log "    $(printf '%-22s' "$d") RESOLVE FAILED"; fi
done

# 3) ICMP ping (RTT + loss) ------------------------------------------------------
hr; log "[3] ICMP ping (10 pkts: avg RTT + loss)"
for p in "${PING_TARGETS[@]}"; do
  res=$(ping -c 10 -w 12 "$p" 2>/dev/null)
  loss=$(echo "$res" | grep -oE '[0-9]+% packet loss' | head -1)
  rtt=$(echo "$res"  | grep -oE 'min/avg/max[^=]*= [^ ]*' | awk '{print $NF}')
  log "    $(printf '%-10s' "$p") loss=${loss:-n/a}  rtt(min/avg/max/mdev)=${rtt:-n/a} ms"
done

# 4) TCP connect latency :443 ----------------------------------------------------
hr; log "[4] TCP+TLS connect latency :443 (3 tries each)"
for hp in "${TCP_TARGETS[@]}"; do
  host="${hp%%:*}"; port="${hp##*:}"
  best=""
  for i in 1 2 3; do
    tc=$(curl -s -m8 -o /dev/null -w '%{time_connect}|%{time_appconnect}' "https://$host" 2>/dev/null)
    [ -n "$tc" ] && best="$tc" && break
  done
  if [ -n "$best" ]; then
    tcp_s="${best%%|*}"; tls_s="${best##*|}"
    log "    $(printf '%-22s' "$hp") tcp=$(awk "BEGIN{printf \"%.0f\",$tcp_s*1000}")ms tls=$(awk "BEGIN{printf \"%.0f\",$tls_s*1000}")ms"
  else
    log "    $(printf '%-22s' "$hp") CONNECT FAILED"
  fi
done

# 5) HTTP reachability (the key check) ------------------------------------------
hr; log "[5] HTTP reachability — status + TTFB + total + bytes"
for u in "${HTTP_TARGETS[@]}"; do
  r=$(curl -s -A 'Mozilla/5.0 vpn-diag' -m 20 -o /dev/null \
      -w '%{http_code}|%{time_starttransfer}|%{time_total}|%{size_download}' "$u" 2>/dev/null)
  if [ -n "$r" ]; then
    code="${r%%|*}"; rest="${r#*|}"; ttfb="${rest%%|*}"; rest="${rest#*|}"; tot="${rest%%|*}"; sz="${rest##*|}"
    verdict="ok"; [ "$code" = "000" ] && verdict="**UNREACHABLE**"
    [ "$code" != "000" ] && [ "$code" -ge 400 ] 2>/dev/null && verdict="http-$code"
    log "    $(printf '%-30s' "$u") code=${code} ttfb=$(awk "BEGIN{printf \"%.0f\",$ttfb*1000}")ms total=$(awk "BEGIN{printf \"%.0f\",$tot*1000}")ms bytes=${sz} ${verdict}"
  else
    log "    $(printf '%-30s' "$u") **UNREACHABLE (curl failed)**"
  fi
done

# 6) Throughput -----------------------------------------------------------------
# Cachefly is unthrottled on most paths; Cloudflare __down is throttled from some
# networks (e.g. RU returns ~20 KB instead of the requested size), so use Cachefly
# as primary with Cloudflare as fallback.
hr; log "[6] Throughput (download)"
dn=""
for src in "https://cachefly.cachefly.net/10mb.test" "https://speed.cloudflare.com/__down?bytes=25000000"; do
  r=$(curl -s -m 40 -o /dev/null -w '%{speed_download}|%{size_download}|%{http_code}' "$src" 2>/dev/null)
  bps="${r%%|*}"; rest="${r#*|}"; sz="${rest%%|*}"; code="${rest##*|}"
  if [ -n "$bps" ] && [ "${sz:-0}" -gt 1000000 ] 2>/dev/null; then
    log "    download: $(awk "BEGIN{printf \"%.1f\",$bps/125000}") Mbit/s  ($(awk "BEGIN{printf \"%.1f\",$bps/1048576}") MiB/s, ${sz} bytes via ${src##*/})"
    dn="ok"; break
  fi
done
[ -z "$dn" ] && log "    download: FAILED/throttled on all sources (got ${sz:-0} bytes, code ${code:-?})"
# Upload (best-effort; some paths throttle the endpoint).
up=$(head -c 8000000 /dev/zero 2>/dev/null | curl -s -m 30 -o /dev/null -w '%{speed_upload}|%{size_upload}' \
     --data-binary @- "https://speed.cloudflare.com/__up" 2>/dev/null)
ubps="${up%%|*}"; usz="${up##*|}"
if [ -n "$ubps" ] && [ "${usz:-0}" -gt 1000000 ] 2>/dev/null; then
  log "    upload:   $(awk "BEGIN{printf \"%.1f\",$ubps/125000}") Mbit/s (${usz} bytes)"
else log "    upload:   best-effort failed/throttled (skip)"; fi

# 7) Path MTU probe (DF flag) ----------------------------------------------------
hr; log "[7] Path-MTU probe (largest unfragmented to ${MTU_TARGET})"
mtu_found=""
for payload in 1472 1464 1422 1392 1352 1252; do   # +28 (IP+ICMP) = 1500/1492/1450/1420/1380/1280
  if ping -c1 -W2 -M do -s "$payload" "$MTU_TARGET" >/dev/null 2>&1; then
    mtu_found=$((payload+28)); log "    OK at payload ${payload} -> path MTU >= ${mtu_found}"; break
  else
    log "    frag/blocked at payload ${payload} (MTU $((payload+28)))"
  fi
done
[ -z "$mtu_found" ] && log "    (no size succeeded — ICMP may be blocked on this path)"

hr; log "Report saved: ${OUT}"
log "Tip: run as './vpn-diag.sh baseline' then './vpn-diag.sh vpn-full' and diff."
