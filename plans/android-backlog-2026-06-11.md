# Android backlog — perf + bug audit (queued after Windows dns-tunnel track)

**Date logged:** 2026-06-11
**Priority:** next major focus after the Windows v2.42.0 dns-tunnel work settles
**Source:** user report (reproduced on the user's phone AND a tester's phone)

## B1 [P0] — WL-BYPASS (dns-tunnel) freezes the app; only a force-stop recovers

**Symptom:** after enabling WL-BYPASS (dns-tunnel) the app freezes / becomes laggy
and unresponsive; it only recovers after a full **force stop** of the app. Seen
on **two different phones** (user + a tester) → not device-specific.

**Strong hypothesis (link to the Windows finding):** this is very likely the
Android app's *poor handling of the same dns-tunnel drop* we just root-caused on
Windows — the recursive НСДИ resolver rate-limits the covert query stream after
~1.5-3 min → QUIC idle-timeout (`0x433`) → tunnel dies. On Windows it fails over
cleanly; on Android it appears to **wedge** (UI/service stuck until process kill),
which points at a main-thread block or a deadlock around the slipstream JNI
lifecycle rather than a graceful failover.

**Where to look (Android dns-tunnel path):**
- `VpnRouterService.java`: `startSlipstreamIfNeeded` / `waitForLocalPort` /
  `stopSlipstreamIfRunning`. A past bug here was a **readiness poll on the main
  thread** (`NetworkOnMainThreadException`, fixed by moving to a worker thread) —
  check for any *remaining* blocking call on the service/UI thread (native
  `nativeStart`/`nativeStop`, socket connect, libbox handoff).
- The JNI crate (`slipstream-android`, `nativeStop` via `tokio::select!`+`Notify`):
  does `nativeStop` block / can it deadlock if the tunnel is mid-reconnect? When
  the QUIC connection is looping reconnect (backoff to 5s) and the user
  toggles/stops, is there a join that never returns?
- Does the app have an equivalent of the desktop StrictDns/health failover for
  the dns-tunnel drop? Careful: desktop StrictDns failover is a `dns.final`
  policy fix; Android likely needs a **transport/lifecycle watchdog** instead:
  detect Slipstream/libbox stall, broadcast a user-visible error or restart, and
  guarantee Stop/Disconnect cannot block forever.
- ANR trace: reproduce, then pull `/data/anr/traces.txt` (or `adb bugreport`) to
  see exactly which thread is blocked.

**Repro harness available:** the adb-reverse + Mac `http.server` subscription
trick (no-paste config injection). Concrete local helpers:
- `tools/slipstream-android-cache/retest_connect.sh`
- `tools/slipstream-android-cache/grab_crash.sh`
- `tools/slipstream-android-cache/probe_7001.sh`
- `tools/slipstream-android-cache/type_dnslink.py`
- background context: `plans/dns-tunnel-android-phase-2026-06-10.md` and
  `plans/dns-tunnel-server-side-2026-06-11.md`.

**Likely missing instrumentation before fixing:**
- Timestamped log markers around `startTunnel` phases: `ensureLibboxSetup`,
  `nativeStart`, `waitForLocalPort`, `boxService.start`, `boxService.close`,
  `nativeStop`, `sendBroadcast(ACTION_TUNNEL_*)`.
- Thread-name logging for every potentially blocking call above; the proof we
  need is "not Android main / service main thread".
- JNI-side `nativeStop` duration + timeout/error logging. If Rust waits on a
  join handle, log the state before waiting and after return.
- A last-known health signal for dns-tunnel, separate from "singbox.log grew":
  local `127.0.0.1:7001` front alive, Slipstream reconnect loop state, and recent
  successful DNS-over-QUIC activity if available.

## B2 [P1] — Advanced mode kicked back to Simple mode on config start

**Symptom:** in **advanced mode**, starting a config throws the user back to
**simple mode** right before launch.

**Correction after static check:** Android does **not** persist desktop-style
`ui_mode` / `UiMode`. The relevant state is:
- `AndroidStorage.Get/SetAdvancedActiveTab()` (`advanced_active_tab`) — last tab.
- `_advShellOverlay.IsVisible` and `_advShellSelectedTab` — transient "Advanced
  shell is open" state.
- `RebuildSimplePageView()` already has a restore path for theme/language rebuilds
  (`advancedWasOpen`, `advancedTab`), so the connect path likely bypasses that
  preservation or closes/rebuilds the overlay indirectly.

**Where to look:** Android connect/apply flow:
- `AndroidApp.VpnLifecycle.cs`: `OnConnectClicked`, `OnIntentChanged`,
  `UpdateConnectionState`.
- `MainActivity.cs`: `DispatchTunnelStart`, `SetIntent(true)`,
  `ACTION_TUNNEL_UP`, `ACTION_TUNNEL_ERROR`.
- `AndroidApp.AdvancedShell.cs`: `OpenAdvancedShell`, `CloseAdvancedShell`,
  `SelectAdvancedTab`.
- Any call from connect/apply that invokes `RebuildSimplePageView`, `CloseAdvancedShell`,
  `ReloadServerList`, or switches the `SingleViewApplicationLifetime.MainView`.

**Fix direction:** starting VPN from the Advanced footer should preserve the
Advanced shell and selected tab through Connecting → Connected/Error. If the
shell must rebuild, capture/restore the same state pattern as `RebuildSimplePageView`.

## B3 [P1] — General Android performance + bug audit

The user asked for a thorough **perf + bug audit** on Android (the freeze is the
headline, but sweep broadly). Candidate areas:
- Main-thread work during connect/apply (the freeze suggests UI-thread blocking).
- Servers list regression check: the old O(N²) "Test all" rebuild bug is already
  fixed by `ScheduleServerListRebuild()` in `AndroidApp.ServerList.cs`, so do not
  redo it as open work. Re-verify with 100/500 servers only as a scale regression
  guard.
- Battery / no-doze behaviour under a live tunnel.
- libbox lifecycle + memory under sustained connection.

Additional audit targets that are missing from the first draft:
- Connect/disconnect idempotency under rapid taps: Connect → Stop → Connect,
  Stop while `nativeStart`/`boxService.start` is in-flight, and Stop after
  dns-tunnel has entered reconnect backoff.
- UI dispatcher pressure while connected: diagnostics timer, health probe,
  Advanced footer uptime mirror, server-list background work, and any repeated
  `Dispatcher.UIThread.Post` burst.
- Native memory growth: libbox + Slipstream JNI over 15/30/60 min, including
  disconnect/reconnect cycles.
- Error surfacing: every service-side failure should produce `ACTION_TUNNEL_ERROR`
  and leave the UI actionable, not stuck in "Connected" or "Connecting".
- Storage writes on hot paths: SharedPreferences `Commit()`/large JSON writes
  during connect, server testing, and config apply.
- Device variance: reproduce on both phones from the report, plus one slower
  phone/emulator if available; failures only on budget hardware still count.

## Static Android code-review addendum (2026-06-12)

These are the extra findings from a static pass over `VPNRouter.Android`. They
should be treated as candidates for measurement first, then fixed in priority
order.

### A1 [P0] Service start/stop lifecycle can block the service main thread

`VpnRouterService.onStartCommand` calls `startTunnel()` and `stopTunnel()`
directly for `ACTION_START` / `ACTION_STOP`. Inside that path:
- `startTunnel()` calls `ensureLibboxSetup`, `startSlipstreamIfNeeded`,
  `startLibboxService`, and `persistLastGoodConfig` synchronously.
- `startSlipstreamIfNeeded()` calls `SlipstreamNative.nativeStart(...)` and then
  waits for readiness via `waitForLocalPort`.
- `waitForLocalPort` moved socket connect onto a worker thread, but still
  blocks the caller with `join(timeoutMs + 2000)`.
- `startLibboxService()` calls `Libbox.checkConfig`, `Libbox.newService`, and
  `boxService.start()` synchronously.
- `stopTunnel()` calls `boxService.close()` and `SlipstreamNative.nativeStop()`
  synchronously.

This is the most suspicious structural match for the reported "freeze until
force-stop" symptom: even if the UI thread is separate, the foreground service
main thread can wedge during native/libbox lifecycle and leave broadcasts/UI
state stuck.

Fix direction:
- Put VPN lifecycle on a single dedicated `HandlerThread` or executor.
- `onStartCommand` should enqueue work and return promptly.
- Make Start/Stop a real state machine: `Idle`, `Starting`, `Running`,
  `Stopping`, `Failed`.
- Bound `nativeStop`, `boxService.close`, and readiness waits with explicit
  timeout logging and an error broadcast.
- UI should not mark disconnected until service confirms stop or reports a
  bounded stop failure.

### A2 [P0] Duplicate start/stop intents are not fully idempotent

The non-action service restore path has a guard for `boxService != null`, but
the explicit `ACTION_START` branch always writes pending config fields and calls
`startTunnel()`. Rapid Connect, config apply while connecting, or reconnect
after a partial failure can start a second lifecycle while the first one still
owns a `ParcelFileDescriptor`, libbox service, or Slipstream instance.

Fix direction:
- Reject duplicate Start while `Starting`/`Running`, or serialize it as
  `Stop old -> Start new`.
- Reject Stop while already `Stopping`, but keep a completion/error callback.
- Log every transition with config generation id, server tag, and thread name.

### A3 [P1] Network callback work is registered on the main Looper

`NetworkInterfaceIterator` uses `new Handler(Looper.getMainLooper())` for
default-network callbacks. Its `fireUpdate` retry path can call `Thread.sleep`
on that handler and then call `updateDefaultInterface` into libbox. A network
change while the tunnel is degraded can therefore add visible UI/main-loop
pressure exactly when the app needs to stay responsive.

Fix direction:
- Register callbacks on a service `HandlerThread`.
- Remove `Thread.sleep` from callback delivery; use delayed posts or a small
  background retry loop.
- Measure Wi-Fi/cellular toggle and airplane-mode recovery while WL-BYPASS is
  in reconnect backoff.

### A4 [P1] SharedPreferences `Commit()` is used on UI hot paths

`AndroidStorage.SetString`, `SetBool`, and `SetInt` use synchronous
`SharedPreferences.Editor.Commit()`. Many UI handlers call these directly:
per-app package toggles, custom categories, split-routing mode, tab selection,
and server selection. This can turn frequent UI interactions into synchronous
disk writes.

Fix direction:
- Use `Apply()` for ordinary UI preferences.
- Keep `Commit()` only for truly recovery-critical values.
- Debounce or batch large JSON writes such as per-app package lists and custom
  categories.
- Add timing logs for writes over 50 ms.

### A5 [P1] Subscribe aggregated "Test all" still has an O(N squared) rebuild

The Servers tab has the coalesced `ScheduleServerListRebuild()` path, but the
Subscribe aggregated list still posts `RebuildAggregatedServerList` after each
probe result. Since the rebuild clears and recreates the whole visible table,
N probe results can cause N full-table rebuilds.

Fix direction:
- Add the same coalesced rebuild pattern to `AndroidApp.SubscribePage.cs`.
- Verify Subscribe "Test all" with 100 and 500 servers, not only the Servers
  tab.

### A6 [P1] Diagnostics use inconsistent sing-box log locations

The runtime and health probe use `FilesDir/singbox.log`, but the in-app log
viewer and diagnostics exporter still read `GetExternalFilesDir(null)` in some
paths. This can make a freeze report look empty or stale right when logs are
most needed.

Fix direction:
- Centralize Android log paths in one helper.
- Prefer `FilesDir/singbox.log` and include legacy external paths only as
  migration/fallback attachments.
- Diagnostics export should include `singbox.stderr.log`, app logcat markers,
  last tunnel state, and Slipstream lifecycle timing.

### A7 [P2] Health check action runs heavy work on the UI thread

`OnMenuHealthCheckClicked` runs `HealthCheck.RunAll()` and file write inline in
the click handler. If DNS/network probes stall, the diagnostics menu itself can
jank or appear frozen.

Fix direction:
- Move the health check to `Task.Run`.
- Disable the menu action while running and surface a progress state.
- Re-enable even on exception/cancellation.

### A8 [P2] Several Android lists are manually rebuilt without virtualization

Servers, Subscribe aggregated rows, and the app picker build controls manually
and replace whole item collections. The app picker also eagerly converts app
icons. This is tolerable at small scale, but with system apps enabled or 500
server rows it can create layout/GC bursts.

Fix direction:
- Measure layout time and GC during app search, per-app toggles, and server
  probe result bursts.
- Prefer data-bound item templates with virtualization where Avalonia Android
  supports it.
- If virtualization is not practical, at least coalesce rebuilds, lazy-load
  icons, and update rows incrementally.

### A9 [P2] Free-config deep verify is intentionally expensive

`AndroidFreeConfigDeepVerifier` can spin up libbox for primary and secondary
checks per candidate. It runs off the UI thread and has guards against connecting
while busy, but the cost is still battery/thermal significant on phones.

Fix direction:
- Measure CPU, memory, battery temperature, and cancellation latency during deep
  verify on slow hardware.
- Make the UI cancellation path explicit and verify that it stops work quickly.
- Avoid running deep verify concurrently with a live VPN service.

### A10 [P2/P3] Android platform bridges need resilience review

`systemCertificates()` enumerates `AndroidCAStore` each call; cache the PEM list
if libbox calls it repeatedly. `localDNSTransport()` returns `null`, which may be
fine today, but should be explicitly reviewed for private DNS, captive portals,
and network-specific resolver behavior.

### A11 [P3] Broad Android permissions should be justified

`QUERY_ALL_PACKAGES` is used for the app picker and `REQUEST_INSTALL_PACKAGES`
for the updater flow. This is acceptable for sideload-oriented distribution, but
needs a written justification or narrower fallback if Play-style policy ever
matters.

### A12 [P1] VPN secrets may be included in Android backup by default

The service persists `last_good_config_json` in `SharedPreferences`; that config
can contain VLESS UUIDs, server hosts, Reality public keys, and dns-tunnel
parameters. `AndroidManifest.xml` does not declare `android:allowBackup`,
`android:dataExtractionRules`, or `android:fullBackupContent`, so the default
backup/data-transfer behavior should be treated as unsafe until verified.

Fix direction:
- Prefer `android:allowBackup="false"` for this app class, or provide explicit
  backup/data-extraction rules that exclude `vpnrouter_settings` and any config,
  crash, and log files containing secrets.
- If backup is intentionally supported later, split non-secret UI prefs from
  tunnel credentials and store secrets in a non-backed-up or encrypted location.

## Performance verification plan

**Devices / environment:**
- Primary user phone where WL-BYPASS freezes.
- Tester phone where the same freeze was reproduced.
- Optional slower device/emulator for worst-case UI thread pressure.
- Use the Mac adb path if running through the Mac host: `/opt/homebrew/bin/adb`.
- Package: `com.ninitux.vpnrouter`.

**Before each measured run:**
- `adb shell am force-stop com.ninitux.vpnrouter`
- `adb logcat -c`
- `adb shell dumpsys gfxinfo com.ninitux.vpnrouter reset`
- Confirm battery saver / Doze state and whether VPNRouter is battery-whitelisted.
- Record app version, ABI, Android version, device model, and whether the APK is
  debug-signed or release-signed.

**Scenarios to measure:**
- Cold launch to first usable frame.
- Open Advanced shell, switch every tab, return to Simple.
- Start normal VLESS server from Simple.
- Start normal VLESS server from Advanced footer; verify it stays in Advanced.
- Start WL-BYPASS dns-tunnel, wait 5-7 min for the known resolver rate-limit
  window, then Stop/Disconnect.
- While WL-BYPASS is in the degraded state: tap Stop, Connect again, switch tabs,
  open kebab, and background/foreground the app.
- Server list "Test all" with 5, 100, and 500 synthetic/cached servers.
- Subscribe aggregated "Test all" with 5, 100, and 500 synthetic/cached servers.
- Rapid Connect/Stop/Connect and duplicate Start intents while `nativeStart` or
  `boxService.start` is still in-flight.
- Wi-Fi/cellular/airplane-mode changes while WL-BYPASS is reconnecting.
- Per-app picker with user apps only, then system apps enabled: search, toggle
  many apps, switch categories, and save custom categories.
- Open diagnostics: log viewer, export diagnostics, and health check while the
  VPN is connected and while WL-BYPASS is degraded.
- 30-60 min sustained tunnel with screen off/on and Doze forced if practical.

**Metrics / artifacts to capture:**
- Logcat with timestamps:
  `adb logcat -v threadtime > android-perf-logcat.txt`
- ANR traces or full bugreport after any freeze:
  `adb shell cat /data/anr/traces.txt` or `adb bugreport`.
- Frame timing:
  `adb shell dumpsys gfxinfo com.ninitux.vpnrouter framestats`
- Memory:
  `adb shell dumpsys meminfo com.ninitux.vpnrouter`
- CPU/thread pressure during connect and freeze:
  `adb shell top -H -p <pid> -d 1`
- App/service state:
  `adb shell dumpsys activity services com.ninitux.vpnrouter`
- VPN/network state:
  `adb shell dumpsys connectivity`
- Private logs via `run-as` on debuggable build:
  `singbox.log`, `singbox.stderr.log`, crash logs, and any Slipstream JNI log.

**Pass/fail thresholds:**
- No ANR dialog and no need for force-stop in any scenario.
- Stop/Disconnect returns UI to actionable disconnected state within 3 s.
- Connect path has no single main-thread stall over 250 ms in logs/traces.
- `nativeStop`/`boxService.close` have bounded duration; if they exceed the
  timeout, the UI still receives an error and remains usable.
- Server-list 100/500 test does not produce long jank bursts or N full list
  rebuilds per N probe results.
- Memory after disconnect returns near baseline; no monotonic growth across
  repeated dns-tunnel reconnect cycles.

## Done-criteria

- B1: WL-BYPASS can be enabled + the tunnel can drop (rate-limit) WITHOUT freezing
  the app — it fails over or surfaces an error, no force-stop needed. Verified on
  both reported devices. Artifacts attached: logcat, ANR/bugreport if triggered,
  frame stats, meminfo, and service logs.
- B2: starting a config from Advanced stays in Advanced on success and on error.
  Verified on device, including selected-tab preservation.
- B3: an audit doc with measured findings + gated fixes, mirroring the Windows
  perf-audit approach (`plans/windows-performance-audit-2026-06-11.md`).

## Follow-up added 2026-06-13 (task_b0cad072 investigation)

**B4 [P2, device-confirm] — `vpn-lifecycle` executor thread count.** During
v2.42.0-r17 device testing, `/proc/<pid>/task/*/comm` showed **7** live
`vpn-lifecycle` threads (6 after a disconnect) where a single-thread executor
should hold ~1. Code analysis (see
`plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md`
"Secondary finding"): `lifecycleExecutor` is a per-`VpnRouterService`-instance
`newSingleThreadExecutor`, shut down in `onDestroy` → **self-cleaning by
construction** (each Connect-after-full-Stop mints a new instance + worker;
`shutdown()` reaps it once its final task drains). The observed 7 is most
consistent with **transient pile-up** during the rapid relaunch/connect storm
(a stuck dns-tunnel QUIC-backoff teardown keeps a worker busy up to ~8s via the
B1 `runBounded` 4s caps), NOT a proven per-connect leak.

Device-confirm before treating as a leak:
- `adb shell "cat /proc/<pid>/task/*/comm | grep -c vpn-lifecycle"` at idle,
  then after N deliberate connect→(wait TUNNEL_UP)→disconnect→(wait ~10s)
  cycles. If the **steady-state idle** count climbs monotonically and does not
  settle back to ~1, it's a real leak.
- Cheap hardening if confirmed: give the executor a core thread that times out
  (replace `newSingleThreadExecutor` with a 1-max `ThreadPoolExecutor` +
  `allowCoreThreadTimeOut(true)`) so an idle drained worker self-reaps.

**Status-card stale "Connected" (the other r17 follow-up):** multi-instance
desync hypothesis **disproven** (test artifact — Avalonia 12 = one AndroidApp
per process). A distinct real residual desync (lost broadcast + no resume
re-sync) was found and **fixed 2026-06-13** (service persists `tunnel_live`;
`MainActivity.OnResume` demote-only re-sync; `TunnelStateResync` + unit test;
vestigial `s_currentLifecycleSubscriber` removed). Needs on-device DKA
verification before/at ship. Full write-up:
`plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md`.
