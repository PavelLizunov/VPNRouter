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
#   LAN_IP=192.168.31.1 EXPECT_LAN_IF=wlan0 bash tools/...              # + P0.1 LAN-bypass gate (T3.5/T3.6)
#   TEST_DISCONNECT=1 bash tools/...                                    # + P0.2 disconnect-recovery gate (T12-T15; tears down tunnel LAST)
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

# ── T3.5/T3.6 — LAN / local-network bypass (P0.1 invariant) ──
# Local/private IPs must NEVER route via tun0, split OR full. Opt-in: set
#   LAN_IP=<router/NAS ip>  [LAN_HTTP_URL=http://ip/]  [EXPECT_LAN_IF=wlan0]
if [ -n "${LAN_IP:-}" ]; then
  LANROUTE="$(ash "ip route get $LAN_IP 2>/dev/null")"
  say "INFO T3.5  ip route get $LAN_IP -> ${LANROUTE:-<none>}"
  if echo "$LANROUTE" | grep -q 'dev tun0'; then
    say "FAIL T3.5  LAN $LAN_IP routes via tun0 — local-network invariant VIOLATED (LAN captured)"
  else
    say "PASS T3.5  LAN $LAN_IP bypasses tun0"
    [ -n "${EXPECT_LAN_IF:-}" ] && ! echo "$LANROUTE" | grep -q "dev ${EXPECT_LAN_IF}" \
      && say "WARN T3.5  route does not mention expected iface ${EXPECT_LAN_IF}"
  fi
  if [ -n "${LAN_HTTP_URL:-}" ]; then
    LCODE="$(ash "curl -s --max-time 8 -o /dev/null -w '%{http_code}' '$LAN_HTTP_URL'")"
    { [ "${LCODE:0:1}" = "2" ] || [ "${LCODE:0:1}" = "3" ]; } \
      && say "PASS T3.6  LAN HTTP $LAN_HTTP_URL -> $LCODE (LAN reachable while connected)" \
      || say "FAIL T3.6  LAN HTTP $LAN_HTTP_URL -> ${LCODE:-timeout}"
  fi
else
  say "INFO T3.5  LAN checks skipped (set LAN_IP=<router/NAS ip> [LAN_HTTP_URL=] [EXPECT_LAN_IF=wlan0])"
fi

# ── T4 — real download throughput ──
# Multi-stream: single-stream curl through a proxy badly under-reports (2.1 Mbps
# single vs 84 Mbps fast.com on the same exit). N parallel streams track real speed.
NS="${NS:-4}"
SPEEDS="$(ash "for i in \$(seq 1 $NS); do curl -s --max-time 60 -o /dev/null -w '%{speed_download}\n' '$DL_URL' & done; wait")"
TOT="$(echo "$SPEEDS" | awk '{s+=$1} END{printf "%.0f", s}')"
MBPS="$(awk "BEGIN{printf \"%.1f\", (${TOT:-0}*8)/1000000}")"
awk "BEGIN{exit !($MBPS>5)}" \
  && say "PASS T4  multi-stream throughput=${MBPS} Mbps (${NS} streams, tun0=$TUN)" \
  || say "WARN T4  multi-stream throughput=${MBPS} Mbps (<5 Mbps, ${NS} streams, tun0=$TUN)"

# ── T5 — latency via TTFB. ICMP ping AND the TCP handshake are answered LOCALLY by
#   the gvisor user-stack (~3ms), so only time-to-first-byte traverses the full
#   phone->exit->origin path and reflects real tunnel latency. ──
TC="$(ash "curl -s --max-time 12 -o /dev/null -w '%{time_starttransfer}' https://1.1.1.1/cdn-cgi/trace")"
TCMS="$(awk "BEGIN{printf \"%.0f\", ${TC:-0}*1000}")"
say "INFO T5  TTFB to Cloudflare = ${TCMS} ms (real tunnel latency; ICMP/TCP-handshake are gvisor-local)"

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

# ── T8 — battery drain: fake-unplug so batterystats accrues even on USB ──
# `dumpsys battery unplug` makes the framework treat the device as on-battery so
# batterystats attributes power; we reset, wait, read the app's mAh, then restore.
BAT_WIN="${BAT_WIN:-120}"
ash dumpsys battery unplug >/dev/null 2>&1
ash dumpsys batterystats --reset >/dev/null 2>&1
say "INFO T8  battery: fake-unplugged, accruing ${BAT_WIN}s idle-connected..."
sleep "$BAT_WIN"
PWR="$(ash "dumpsys batterystats --charged $PKG" | grep -iE "uid [0-9u]+:.*mah" | head -1)"
ash dumpsys battery reset >/dev/null 2>&1   # CRITICAL: restore real charging state
say "INFO T8  battery over ${BAT_WIN}s: ${PWR:-<no per-uid mAh accrued — try longer BAT_WIN>}"

# ── T9 — stability: tun0 still up after the test window (NAT-idle 5-min drop) ──
say "$([ "$(ash 'ip addr show tun0 2>/dev/null | grep -q inet && echo up || echo down')" = up ] && echo 'PASS' || echo 'FAIL') T9  tun0 after window: $(ash 'ip addr show tun0 2>/dev/null | grep -q inet && echo up || echo down')"

# ── T12-T15 — explicit-disconnect recovery (P0.2). OPT-IN: it tears down the
#   tunnel, so it runs LAST and only when TEST_DISCONNECT=1. Uses the app's own
#   STOP service action (not force-stop) so it exercises the real teardown path. ──
if [ "${TEST_DISCONNECT:-0}" = "1" ]; then
  say "---- T12-T15 explicit-disconnect recovery gate ----"
  say "INFO T12  pre-disconnect tun0=$(ash 'ip addr show tun0 2>/dev/null | grep -q inet && echo up || echo down')"
  ash am startservice -n "$PKG/.VpnRouterService" -a "$PKG.STOP" >/dev/null 2>&1 \
    || ash am start-service -n "$PKG/.VpnRouterService" -a "$PKG.STOP" >/dev/null 2>&1
  sleep 6
  TUN2="$(ash 'ip addr show tun0 2>/dev/null | grep -q inet && echo up || echo down')"
  [ "$TUN2" = down ] && say "PASS T12  tun0 removed after explicit disconnect" \
                     || say "FAIL T12  tun0 still $TUN2 after explicit disconnect"
  ACODE="$(ash 'curl -s --max-time 12 -o /dev/null -w "%{http_code}" https://www.google.com')"
  { [ "${ACODE:0:1}" = "2" ] || [ "${ACODE:0:1}" = "3" ]; } \
    && say "PASS T13  DNS/HTTPS works after disconnect (HTTP $ACODE, direct/ISP path)" \
    || say "FAIL T13  DNS/HTTPS broken after disconnect (HTTP ${ACODE:-timeout})"
  DIP="$(ash curl -s --max-time 12 "$IP_URL")"
  say "INFO T14  post-disconnect egress IP=${DIP:-<none>} (should be home/ISP, not the T2 exit ${EXIT_IP:-?})"
  if $ADB logcat -d 2>/dev/null | tail -400 | grep -qE 'ACTION_RESTART|system-initiated start|libbox service started'; then
    say "WARN T15  a restart/re-start log appeared after explicit stop — verify it was NOT triggered here (Always-on?)"
  else
    say "PASS T15  no ACTION_RESTART / tunnel re-start after explicit stop"
  fi
else
  say "INFO T12  disconnect-recovery gate skipped (set TEST_DISCONNECT=1 — it tears down the tunnel)"
fi

say "================ report: $OUT/report.txt ================"
