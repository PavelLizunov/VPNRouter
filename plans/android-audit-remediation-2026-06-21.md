# Android audit remediation — disposition of every item (2026-06-21)

Source: the 4-agent audit (leak / best-practices / peer-apps) + the live device tests.
This file dispositions EVERY audit item: done, no-change-needed, defer-attended, or
feature-brief. Autonomous-run principle: ship the safe high-value fixes tested; do
NOT rush VPN-core / security-surface / large-feature changes unattended (a half-baked
one is worse than none) — give a ready-to-execute plan instead.

## DONE — implemented, device-tested (A101BM), regression-green, shipped
| Item | What | Commit |
|---|---|---|
| **B1** | POST_NOTIFICATIONS requested at first launch on Android 13+ (FGS notification + Disconnect were suppressed until granted). | 279ffa98 |
| **B2** | `StartTunnelService` async + `BuildConfigJson*` in `Task.Run` → connect tap no longer blocks the UI thread (ANR risk). Device-verified: off-thread connect → tun0 up, no crash. | 279ffa98 |
| **B8** | Battery-opt exemption re-prompts (24h-throttled) while not exempt, vs one-shot — the single biggest reliability lever. | 279ffa98 |
| **L1** | Hard count-cap (300) on server-test-results on top of the existing 7-day age-out. (Audit missed the 7-day prune — growth was already time-bounded; this is the explicit backstop.) | 279ffa98 |
| **B7** | Localized FGS notification (text + "Disconnect") passed as intent extras (C# `Localization` → Java `VpnRouterService`, English literals as fallback). | this run |

Regression: 32 unit/characterization tests green; AndroidApp source-hash unchanged
(no member changes). Device: install + off-thread connect + urltest active + no crash.

## NO CHANGE NEEDED — reviewed, current behavior correct
- **B5 (DNS fallback)**: `openTun` uses the config's DNS when present; `1.1.1.1` is a
  last-resort fallback ONLY when `TunOptions` yields no DNS (a broken-config case where
  *some* resolver beats none). Config DNS already takes precedence — this is not a leak
  and not silent in the normal path. Changing the fallback risks DNS breakage for the
  no-DNS case. Optional future: make the last-resort configurable. **No change.**
- **L2 (`+=`-in-builder)**: audit verdict was "no change required" — the handlers don't
  leak under the current ownership graph (publisher controls live in the discarded
  overlay subtree; no static/long-lived retainer). Optional hardening only. **No change.**

## DEFER — attended (genuine risk; ready-to-execute approach below)
- **B3 (fail-closed kill-switch)** — the real mechanism is **OS Always-on VPN + Lockdown**
  (a non-privileged Android app *cannot* block other apps' traffic itself). It's already
  reachable via Settings→Reliability deep-link, and `OnResume` demote already corrects the
  OEM-teardown-without-onRevoke stale-card case. Remaining gap = the brief traffic window
  during an undetected teardown — only Lockdown closes it. **Approach:** add a proactive
  one-time "enable Always-on + Lockdown for a kill-switch" nudge (UI, like the B8 battery
  prompt) + a status indicator. Pure UX; no core change. Attended (UI verify on device).
- **B4 (Profiled AOT / R8 / linker)** — `RunAOT=true` is safe (precompile, no reflection
  impact) and improves cold-start but raises build time + APK size; `AndroidLinkMode=Full`
  is the real size win but **risks breaking reflection-loaded Java** (`QrScanLauncher`,
  `AndroidDeepVerifyBox` via `Class.ForName`) without correct ProGuard/linker keep rules.
  **Approach:** measure cold-start first; add keep-rules for the reflection surfaces; then
  enable Full link + AOT; verify QR scan + deep-verify on device. Attended (multi-device).
- **P4 (broadcast control START/STOP/TOGGLE)** — Tasker/widget automation. **Security:**
  an exported VPN-control receiver must require a **signature-level custom permission** so
  arbitrary apps can't toggle the VPN (adb shell + same-signed callers still work).
  **Approach:** declare `<permission android:protectionLevel="signature">` + receiver
  guarded by it → forwards ACTION_START/STOP to the service. Attended (verify perms).

## FEATURES — peer learnings (larger; prioritized brief)
- **P1 (live stats via clash_api)** — DONE 2026-06-21, device-verified on A101BM.
  **Root cause (logcat):** the VPN app's own loopback to 127.0.0.1:9090 was captured by
  its own tun under a full tunnel (`HttpRequestException: Connection failure`); adb's
  `shell` uid bypasses the VPN, which is why external `curl` reached it but the in-process
  managed `HttpClient` couldn't. **Fix shipped:** `VpnRouterService.java` polls clash_api
  `/connections` every 2s over a `VpnService.protect(socket)`ed raw socket (HTTP/1.0,
  close-delimited), parses `downloadTotal`/`uploadTotal` + the `connections[]` count via
  org.json, and broadcasts `ACTION_STATS`. `MainActivity` receives → `StatsReported` event
  → `AndroidApp.OnStatsReported` derives the rate (cumulative-delta / dt) and renders
  `↓ {rate} ↑ {rate} · {n} conn` on the shared status card (change-only write per
  Bug-AND-006, marshalled off the binder thread). Device-verified: conn count live (4→8→9),
  rate non-zero & matching the log delta exactly (e.g. `↓ 12 B/s` ↔ 24-byte/2s delta;
  `downloadTotal` observed climbing to 20 MB). 0 B/s shows honestly when the node passes
  no traffic. This also CORRECTS the stale "libbox doesn't expose that port" comment in
  AndroidConfigBuilder.cs — clash_api IS up on Android. Surface-hash re-pinned 3891b138.
- **P2 (subscription user-info)** — parse the `Subscription-Userinfo` header (upload/
  download/total/expire) in `SubscriptionFetcher` → render a shared Avalonia card
  (remaining traffic + days-left). Additive; high perceived value; M effort.
- **B6 (god-class extraction)** — extract the diagnostics pump + chip state machine from
  AndroidApp into injected collaborator classes with their own tests (cuts the 5k-LOC core
  file + the brittle source-hash churn). Maintainability; L; do incrementally.
- **P6** — multi-tier latency (TCP/HTTP/real-delay) per node + tolerant multi-format
  subscription parser (Clash/v2rayN/sing-box). M; refines existing surfaces.

## Recommended order for the next attended session
P1 (leverage) → P2 (value) → B7 + P4 (bounded) → B4 (measure-then-enable) → B3 nudge.
B6 ongoing. P6 opportunistic.
