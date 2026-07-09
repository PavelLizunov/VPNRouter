# Android e2e test plan (realistic, on-device) — 2026-06-21

Harness: `tools/android-e2e-test.sh` (runs on the Mac host where the test phone is
attached via adb; non-root; uses the phone's toybox `curl`/`ping`). Goal: test
**"does the VPN do its job"**, not "tab rendered". Every test targets a bug class
VPNRouter has actually shipped.

## Why these tests (tied to real failure history)

| Test | Catches | History |
|---|---|---|
| **T6 idle CPU** | overheating / battery drain | shipped ~40% idle CPU once ("phone overheats", AndroidConfigBuilder.cs debug-log fix) |
| **T8/T9 stability** | 5-min NAT-idle disconnect | mobile NAT idle timeout; F4 keepalive stripped on libbox 1.13 |
| **T2 egress IP** | leak (real IP instead of exit) | v2.28.1 silent leak (missing proxy outbound) |
| **T3 DNS/route** | DNS leak / route hole | DNS detour bugs, full-tunnel route.final |
| **T1 urltest** | the A feature actually forms the pool | Bug-AND-A1 (pool read wrong source → single vless, no urltest) |
| **T4 + fast.com** | throughput regression | — |
| **T10 browser (ChatGPT)** | exit-IP reputation / Cloudflare | ChatGPT "Verifying…" on RU datacenter exits (deep-research 2026-06-20) |
| **T7 memory** | leak over a session | Avalonia/Mono/libbox PSS growth |

## Tests

Automated (adb only, no UI — run after the user connects once):
- **T1** urltest active: logcat has `outbound/urltest[proxy]` + lists the VLESS pool members.
- **T2** egress IP + country via `curl api.ipify.org` → must be the exit country, NOT home/RU.
- **T3** `curl https://google.com` → 2xx/3xx (DNS + route OK through tunnel).
- **T4** single-stream `curl` download = a conservative connectivity **floor** (under-reports through a proxy; use fast.com for the real number).
- **T5** `ping` latency (caveat: ICMP may not transit the TUN — informational).
- **T6** connected-idle CPU over 60s from `/proc/<pid>/stat` → **PASS < 10% of 1 core** (overheating guard).
- **T7** `dumpsys meminfo` TOTAL PSS.
- **T8** battery power estimate (`dumpsys batterystats`; needs an unplugged window to be meaningful).
- **T9** `tun0` still up after the window (no NAT-idle drop).

UI (`MODE=ui`, needs the phone unlocked):
- **T10** Chrome → youtube.com / chatgpt.com / fast.com, screenshot each (real-world load; ChatGPT exercises the Cloudflare/exit-IP case).
- **T11** fast.com speed read (multi-stream, the realistic throughput number).

## Live run 2026-06-21 (A101BM, Android 12, DE exit via urltest auto-select)

- T1 PASS — `urltest[proxy]` over Germany/Iceland/Netherlands VLESS.
- T2 — egress 104.194.156.93 / **DE** (foreign exit, no leak).
- T3 — google.com curl timed out (000) on this run, BUT browser loaded youtube + ChatGPT + fast.com → transient/single-stream artifact, not real breakage.
- T4 — single-stream floor 2.1 Mbps; **fast.com multi-stream = 84 Mbps** (real speed, healthy).
- **T6 PASS — idle CPU 6.4% of 1 core** (vs the historical ~40% — no overheating regression). Headline result.
- T7 — PSS 213 MB. T9 PASS — tun0 stable.
- T10 — **ChatGPT loaded with no Cloudflare challenge** through the DE exit (contrast: RU datacenter exits got "Verifying…"). Data point for `warp-outbound-exit-server-chatgpt` backlog.

## Follow-ups — DONE 2026-06-21
- T4 → multi-stream (NS parallel curls): 76.7 Mbps vs 42 single-stream, tracks fast.com's 84. ✅
- T5 → TTFB (time-to-first-byte): ICMP **and** the TCP handshake are answered locally by the gvisor user-stack (~3 ms), so only TTFB traverses phone→exit→origin = real latency. ✅
- T8 → `dumpsys battery unplug` + `batterystats --reset` window + `battery reset` to restore: real per-uid mAh without physically unplugging (needs a longer BAT_WIN for a stable figure). ✅
- **Auto-gate**: harness can't run in GitHub CI (no device). It's the **manual Android post-ship gate** — run `ADB=/opt/homebrew/bin/adb bash tools/android-e2e-test.sh` (+ `MODE=ui`) on the Mac host after every Android ship, same role `post-ship-mcp-verify` plays for desktop.

## Memory-leak verdict (2026-06-21)
**No leak that grows with tunnel uptime.** Empirical (live, A101BM): Views=11 / Activities=1
dead-flat across 8 UI-churn cycles; Native Heap stable ~90 MB; PSS reclaims on trim (1.7 GB
churn spike → ~677 MB steady). Code audit (workflow, 4 agents) confirms: the dangerous classes
(static-event Activity leak AND-011, chip-pulse CTS, mascot stream, QR/export statics) were all
already fixed. Only genuinely usage-correlated growth = stale `_subsAggResults`/`_srvResults`
entries for removed servers (low severity, bounded-prune fix). Footprint is heavy (~500-680 MB)
— the Avalonia+Mono+43 MB libgojni tax; a native Kotlin app is far lighter — but it's footprint,
not a leak. Full audit + best-practices + peer comparison: see session report 2026-06-21.

## P0.1 / P0.2 safety gates — ADDED 2026-07-09 (audit handoff Android P0.1/P0.2)

Opt-in gates in `tools/android-e2e-test.sh` (skipped by default so the standard
post-ship run is unchanged). Pair with the `openTun:` route-materialization logcat
lines (`VpnRouterService.java`) which prove what libbox TunOptions became.

- **T3.5 LAN route bypass** — `LAN_IP=<router/NAS>`: `ip route get $LAN_IP` must NOT
  contain `dev tun0` (local-network invariant). `EXPECT_LAN_IF=wlan0` warns if the
  route doesn't mention the expected iface.
- **T3.6 LAN HTTP probe** — `LAN_HTTP_URL=http://<ip>/`: expect 2xx/3xx while connected.
- **T12 explicit disconnect removes tun0** — `TEST_DISCONNECT=1`: STOP via the app's
  own `.VpnRouterService` STOP action (not force-stop); `tun0` must go down.
- **T13 DNS/HTTPS after disconnect** — public HTTPS must work post-disconnect (direct/ISP).
- **T14 egress restored** — post-disconnect egress IP should be home/ISP, not the T2 exit.
- **T15 no ACTION_RESTART after explicit stop** — logcat must show no tunnel re-start
  (a restart is only acceptable under Always-on/system-requested behaviour).

Ship gate: run with `LAN_IP` set (and `LAN_HTTP_URL` when a LAN target exists), then a
separate `TEST_DISCONNECT=1` pass. Device-only — cannot run in GitHub CI (no phone).
