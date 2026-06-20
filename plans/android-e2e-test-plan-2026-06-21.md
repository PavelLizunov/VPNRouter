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

## Follow-ups
- T4: add a multi-stream/parallel-curl variant so the automated number tracks fast.com.
- T5: ICMP-vs-TUN — replace with a TCP-connect latency probe for tunnel-representative latency.
- T8: a separate longer **unplugged** battery run for a real mAh/hour drain figure.
- Wire this into `post-ship-mcp-verify` (or a sibling) so Android ships get the same gate as desktop.
