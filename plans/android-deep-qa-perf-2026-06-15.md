# Android deep QA + performance campaign

Date: 2026-06-15 (long autonomous device session, user-directed).
Device: A101BM (KYOCERA, Android 12), serial 54499112209, via Mac adb
(`slovn@192.168.0.246` `/opt/homebrew/bin/adb`). Build under test: the on-device
APK (currently v2.42.0 stable + the local A10/A11 + partial-B4 changes already
pushed to `main`).

## Mandate

Exhaustive, slow, thoughtful: click through EVERY UI flow, scroll the browser,
profile performance + resource consumption, fix bugs found, improve responsiveness.
Full decision authority, no resource cap. **Re-check every finding ≥3× before
trusting it** (no single-run conclusions). Device-test is serial (one phone) — no
forced parallelism.

## Methodology

- **Perf harness:** `tools/android-qa/perf_snap.sh "<label>"` → VmRSS, thread count,
  vpn-lifecycle workers, open FDs, tun0 state. Called 3× per measurement point.
- **Jank:** `dumpsys gfxinfo <pkg>` (reset, exercise, read janky-frame %).
- **UI driving:** Avalonia renders to ONE SurfaceView ⇒ uiautomator only sees SYSTEM
  dialogs; the app UI is driven by pixel taps (1080×1920) + screencap read-back.
  Chrome (browse-test) is a normal app, drivable via `am start`/uiautomator.
- **Bug forensics:** release APK blocks `/data/anr/traces.txt`; build a DEBUGGABLE
  APK when Java stacks are needed (e.g., the B4 hung-worker root cause).
- **Logcat:** capture `[ERR]`/`Exception`/`FATAL`/ANR per flow.

## Phases

0. Baseline + harness (memory/threads/FD idle).
1. Exhaustive UI walkthrough — every page/flow, every tappable control, screencap +
   logcat per step.
2. Browse-test loop — connect → Chrome load+scroll multiple HTTPS sites → toggle a
   setting → browse again → repeat; verify real traffic + A10 cert cache.
3. Performance + resource profiling — memory over connect cycles + heavy nav (leaks),
   CPU, gfxinfo jank (scroll/nav), threads/FDs, connect latency, throughput.
4. Root-cause + fix bugs + responsiveness wins (incl. B4 churn/hang via debuggable build).
5. Re-verify each fix on device (≥3×) + commit with evidence.

## Findings log

| # | Phase | Finding | Severity | Re-checks | Status |
|---|---|---|---|---|---|
| F1 | 4 | **B4 "vpn-lifecycle thread leak" is NOT a leak** — bounded + plateaus | INFO (was suspected P2) | 21 connect cycles + 3 instr. builds | RESOLVED — instrumentation reverted, comment corrected |
| F2 | 0/3 | Connect/disconnect path rock-solid: 21/21 connect OK, 21/21 disconnect OK, no ANR, no TIMEOUT | (positive) | 21 cycles | Verified |
| F3 | 1 | "⚠ Stale check" on a HEALTHY but IDLE connection — health probe `_lastHealthOk = grew\|\|recent` keys off sing-box LOG growth; idle tunnel writes no log → flips unhealthy after 60s | P2 (UX, alarming false alarm) | 3 timeline samples + device-confirmed root cause + post-fix re-verify | **FIXED + VERIFIED** — added OS VPN-transport ground truth (`_lastHealthOk = grew\|\|recent\|\|vpnUp`); 75s idle now shows "✓ Last check 1s ago" |
| F4 | 1 | **Theme toggle → RebuildSimplePageView breaks the rebuilt view's live reactivity**: kebab + Config·Mode popups won't reopen AND the status card stops reflecting TUNNEL_UP/DOWN, until app restart | P1 (UX; Simple page partially dead after a mid-session theme change) | reproduced ×3; isolated (plain reopen OK; theme-toggle reopen FAILS; clean connect card OK, post-rebuild connect card STUCK "Not connected") | **PARTIAL fix** (close popup before rebuild → kills orphan-eats-input). Full fix = finish BindToken migration / re-wire after swap → **spawned task** |
| F-RU | 1 | RU language toggle = **FALSE POSITIVE** (works: immediate refresh + persist). Earlier "dead" reading was tainted by F4 stale refs | — | clean fresh-launch test: EN↔RU instant + cold-launch in RU | Not a bug — lesson logged |
| F6 | Settings | **Two unsynced routing-mode keys**: Simple page reads/writes `PerAppMode` (off=All traffic, drives the actual VpnService per-app filter), Advanced Settings→Routing reads/writes a separate `RoutingMode` ("split"/"full"). Manual toggles update only their own key → drift; the Advanced radio can show a state that contradicts Simple AND doesn't drive routing | P2 (confusing; Advanced control misleading/maybe non-functional) | confirmed in code (init from PerAppMode at 1244/1274, re-seed from RoutingMode at 2369) + device (Simple "All traffic" vs Advanced "Split Tunnel", traffic = full-tunnel) | Documented — needs model unification → **spawned task** |
| F7 | 2/3 | **Browse test PASS** + perf: real traffic through tunnel (~1.3 MB), heavy HTTP/2 sites (wikipedia/bbc) render+scroll, no ERR_CONNECTION_CLOSED (MTU-1280 holds on Android); server switch re-apply works (browse-toggle-browse loop) | (positive) | example.com/.org, wikipedia (scrolled), bbc.com/news; tun0 byte deltas | Verified |

### F1 detail — B4 thread accounting (the multi-build root-cause)

Chased as a suspected per-connect thread leak. Re-checked from four architectural lenses:

1. **Service-lifecycle lens** (QA-LC instrumentation: per-instance `onCreate`/
   `onStartCommand`/`onDestroy` counter): exactly ONE service instance (`#1`),
   ONE `onStartCommand`. **Refuted** the "5-6 instance churn" hypothesis.
2. **Executor-semantics lens**: `lifecycleExecutor` is a single-thread pool with an
   unbounded `LinkedBlockingQueue` ⇒ at most 1 live worker by construction. So
   `vpn-lifecycle`>1 is anomalous, but the only thread *source* in our code (verified
   by grep: the sole `new Thread(r,"vpn-lifecycle")` factory + no `Thread.setName`).
3. **Thread-histogram lens** (`/proc/<pid>/task/*/comm`): the growing threads are the
   unnamed `Thread-14..28` series = **libbox/gomobile JNI-attached goroutine M's**,
   NOT our named threads. Our teardown (`teardownTunnelResources`, line ~1067) closes
   the BoxService correctly (capture → null field → bounded `bs.close()` → slipstream
   → pfd → wakelock), so we are not orphaning runtimes.
4. **Plateau lens** (6-cycle then 15-cycle device runs): total threads warm up
   **~39 → ~48 over the first ~6-8 connects** (Go runtime growing its M-pool to steady
   state) then **plateau dead-flat at 48 (connect) / 46 (disconnect) across cycles
   7..21**. `vpn-lifecycle` oscillates **5↔4** (reaped within 30s by the core-timeout
   fix). RSS 309→321 MB then flat. `vpn-bounded`=0 (teardown threads reap inside 4s).

**Conclusion:** bounded + RSS-neutral ⇒ benign. The committed core-timeout reaper
(`newLifecycleExecutor`, `allowCoreThreadTimeOut(true)`) is kept as defense for the
genuinely-orphaned-executor case (START_STICKY recreate without `onDestroy`). The
`vpn-lifecycle`=4-steady-state (>1 for a single-thread pool) is unexplained by pure
JDK `ThreadPoolExecutor` semantics and suspected ART/gomobile interplay, but proven
to NOT grow across 21 cycles, so not pursued further (would need a debuggable build
+ `/data/anr/traces.txt` stacks for a non-actionable curiosity).

### F4 detail — theme-toggle kills the kebab menu (the multi-angle catch)

`ApplyTheme(mode)` (AndroidApp.axaml.cs) does `RequestedThemeVariant = …` then posts
`RebuildSimplePageView()`, which `singleView.MainView = BuildSimplePageView()` — a full
view-tree swap that recreates `_kebabMenuButton` + `_kebabPopup` (Avalonia `Popup`,
`IsLightDismissEnabled=true`, added to `headerRow.Children`). The theme toggle is tapped
from INSIDE the open popup, so the swap happens with the old popup `IsOpen=true`. The
orphaned open popup keeps the top-level overlay/light-dismiss layer; the fresh popup can't
re-acquire it, so `IsOpen=true` on the new popup renders nothing → menu dead until restart.
Isolation proof: fresh launch open→close→reopen WORKS; the same with a Dark tap before the
close FAILS (reproduced twice). Fix = close `_kebabPopup` before the rebuild.

### F-RU lesson — a tainted run produced a confident FALSE bug

Spent ~12 device round-trips "confirming" the RU language button was dead (4 precise taps,
EN stayed selected, even verified the coordinate by crop). It was 100% an artifact: my
earlier Light↔Dark theme toggles (each a `RebuildSimplePageView`) had orphaned the kebab's
cached control-field references via the SAME F4 mechanism, so `ToggleLanguageAndRefresh`
updated stale invisible controls. A clean fresh-launch test showed RU↔EN switching
instantly AND the app cold-launching in the persisted language. **Lesson: a single dirty
session yields a confident wrong conclusion — re-test from a pristine state before
trusting a "dead control" finding.** (Exactly the user's "re-check multiple times from
different architectural viewpoints" mandate paying off.)

## Baseline measurements (A101BM, v2.42.0 + local A10/A11/B4, instrumented build)

| State | RSS | Threads | vpn-lifecycle | Notes |
|---|---|---|---|---|
| Idle-connected (warm) | ~310 MB | 39 | 4 | post-connect settle |
| Connected (steady, post-warmup) | ~321 MB | 48 | 5 | plateau, flat c7..21 |
| Disconnected (steady) | ~321 MB | 46 | 4 | plateau |

tun0 = `172.19.0.1/30` when up. Screen 1080×1920, density 450. Toggle button:
Connect (540,1572) when down, Disconnect (540,1660) when up.
