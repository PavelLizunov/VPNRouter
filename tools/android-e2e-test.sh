#!/usr/bin/env bash
# android-e2e-test.sh — realistic on-device e2e suite for VPNRouter Android.
#
# Runs on the Mac host where the test phone is attached (adb). Non-root; uses the
# phone's toybox `curl`/`ping` so most checks need NO UI (run after the user has
# connected the tunnel once). Each test targets a class of bug VPNRouter has
# actually shipped before — this is "does the VPN do its job", not "tab rendered".
#
# Usage:
#   ADB=/opt/homebrew/bin/adb bash tools/android-e2e-test.sh            # full auto suite
#   MODE=baseline bash tools/android-e2e-test.sh                        # VPN-OFF reference run
#   MODE=ui bash tools/android-e2e-test.sh                              # browser + speedtest (needs unlock)
#
# Recommended flow: run MODE=baseline (VPN off) -> connect in-app (auto-select on)
# -> run default (tunnel) -> compare egress IP / throughput / idle-CPU.
set -uo pipefail
ADB="${ADB:-/opt/homebrew/bin/adb}"
PKG="com.ninitux.vpnrouter"
OUT="${OUT:-/tmp/vpnrouter-e2e}"; mkdir -p "$OUT"
MODE="${MODE:-auto}"
HZ=100
DL_URL="${DL_URL:-https://speed.cloudflare.com/__down?bytes=20000000}"   # 20 MB
IP_URL="${IP_URL:-https://api.ipify.org}"
say(){ echo "$*" | tee -a "$OUT/report.txt"; }
ash(){ $ADB shell "$@" 2>/dev/null | tr -d '\r'; }
shot(){ $ADB exec-out screencap -p > "$OUT/$1.png" 2>/dev/null && say "  screenshot: $OUT/$1.png"; }

P="$(ash pidof "$PKG")"
[ -n "$P" ] || { echo "FAIL T0: $PKG not running — launch it first"; exit 1; }
TUN="$(ash 'ip addr show tun0 2>/dev/null | grep -q "inet " && echo up || echo down')"
say "================ VPNRouter Android e2e ($MODE) ================"
say "device=$(ash getprop ro.product.model)  android=$(ash getprop ro.build.version.release)  pid=$P  tun0=$TUN"

if [ "$MODE" = "ui" ]; then
  # ── T10 browser real-world load (incl. the ChatGPT/Cloudflare exit-IP case) ──
  for url in https://www.youtube.com https://chatgpt.com https://fast.com; do
    name="$(echo "$url" | sed 's#https://##; s#[/.]#_#g')"
    say "T10 launching Chrome -> $url"
    ash am start -a android.intent.action.VIEW -d "$url" -n com.android.chrome/com.google.android.apps.chrome.Main >/dev/null
    sleep 18; shot "ui_$name"
  done
  say "T11 speed test: fast.com left open ~30s — read Mbps off ui_fast_com.png"
  sleep 25; shot "ui_fast_com_result"
  say "UI run done — eyeball the PNGs in $OUT"
  exit 0
fi

# ── T1 — A urltest group active (the ported feature) ──
if $ADB logcat -d 2>/dev/null | grep -q 'outbound/urltest\[proxy\]'; then
  members="$($ADB logcat -d 2>/dev/null | grep -oE 'outbound/vless\[vless-[^]]+\]' | sort -u | tr '\n' ' ')"
  say "PASS T1  urltest[proxy] active; pool members: ${members:-<none in window>}"
else
  say "INFO T1  no urltest[proxy] in recent log (auto-select off, or not connected)"
fi

# ── T2 — egress IP + geo (leak / exit verification) ──
EXIT_IP="$(ash curl -s --max-time 15 "$IP_URL")"
GEO="$(ash curl -s --max-time 15 "https://ipinfo.io/${EXIT_IP}/country")"
say "INFO T2  egress IP=${EXIT_IP:-<none>}  country=${GEO:-?}  (tunnel: must be exit country, NOT home/RU)"

# ── T3 — routed HTTPS + DNS through the active path ──
CODE="$(ash 'curl -s --max-time 15 -o /dev/null -w "%{http_code}" https://www.google.com')"
[ "${CODE:0:1}" = "2" ] || [ "${CODE:0:1}" = "3" ] \
  && say "PASS T3  https://google.com -> HTTP $CODE (DNS + route OK)" \
  || say "FAIL T3  https://google.com -> HTTP ${CODE:-timeout}"

# ── T4 — real download throughput ──
SPEED="$(ash curl -s --max-time 90 -o /dev/null -w '%{speed_download}' "$DL_URL")"
MBPS="$(awk "BEGIN{printf \"%.1f\", (${SPEED:-0}*8)/1000000}")"
# NOTE: single-stream curl through a proxy badly under-reports (measured 2.1 Mbps
# here while fast.com multi-stream showed 84 Mbps on the same DE exit). Treat this
# as a conservative connectivity floor; use MODE=ui fast.com for the real speed.
awk "BEGIN{exit !($MBPS>0.5)}" \
  && say "INFO T4  single-stream floor=${MBPS} Mbps (real multi-stream speed via MODE=ui fast.com)" \
  || say "FAIL T4  single-stream=${MBPS} Mbps — no usable throughput (tun0=$TUN)"

# ── T5 — latency ──
PING="$(ash ping -c 5 -W 2 1.1.1.1 | grep -oE 'avg[^=]*= [0-9./]+' | grep -oE '[0-9.]+' | sed -n '2p')"
say "INFO T5  ping 1.1.1.1 avg=${PING:-?} ms"

# ── T6 — idle CPU over 60s (OVERHEATING regression guard; shipped ~40% once) ──
cpu(){ ash cat /proc/$P/stat | awk '{print $14+$15}'; }
C1="$(cpu)"; say "INFO T6  sampling connected-idle CPU for 60s..."; sleep 60; C2="$(cpu)"
CPUPCT="$(awk "BEGIN{printf \"%.1f\", (($C2-$C1)/$HZ)/60*100}")"
awk "BEGIN{exit !($CPUPCT<10)}" \
  && say "PASS T6  idle CPU=${CPUPCT}% of 1 core (< 10%, no overheating)" \
  || say "FAIL T6  idle CPU=${CPUPCT}% of 1 core (>= 10% — overheating regression!)"

# ── T7 — memory PSS ──
PSS="$(ash dumpsys meminfo "$PKG" | grep -i 'TOTAL PSS' | head -1 | grep -oE '[0-9]+' | head -1)"
say "INFO T7  TOTAL PSS=$(( ${PSS:-0} / 1024 )) MB"

# ── T8 — battery power estimate for the app (approx, charged-window) ──
PWR="$(ash dumpsys batterystats --charged "$PKG" | grep -iE "Uid .*: .*mAh" | head -1)"
say "INFO T8  battery: ${PWR:-<no estimate yet — needs a longer unplugged window>}"

# ── T9 — stability: tun0 still up after the test window (NAT-idle 5-min drop) ──
say "$([ "$(ash 'ip addr show tun0 2>/dev/null | grep -q inet && echo up || echo down')" = up ] && echo 'PASS' || echo 'FAIL') T9  tun0 after window: $(ash 'ip addr show tun0 2>/dev/null | grep -q inet && echo up || echo down')"

say "================ report: $OUT/report.txt ================"
