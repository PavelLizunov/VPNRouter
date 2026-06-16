# Android performance / battery / response audit - 2026-06-15

Scope: independent code walk over `VPNRouter.Android` and Android-specific Core paths.
Goal: mark code locations that can affect performance, battery drain, app responsiveness,
connect latency, and perceived UI lag. This is not a fix plan yet; it is the list of
places worth walking through with measurements.

Related evidence:

- `plans/android-deep-qa-perf-2026-06-15.md` already shows the current A101BM baseline:
  RSS around 310-321 MB connected warm, threads warm to about 48 and then plateau,
  21/21 connect/disconnect cycles passed.
- Earlier suspected B4 thread leak was measured as not a leak: the thread count warmed
  and then stabilized. Do not reopen it as a leak unless new evidence appears.
- The remaining risk is mostly bursty CPU/IO/UI work, background network loops, and
  work performed synchronously on the Activity/UI side.

Legend:

- P0 - inspect first; likely user-visible latency or battery cost under normal flows.
- P1 - important under large data sets, long idle, flaky network, or repeated use.
- P2 - lower risk / rare path / latent hazard, but keep marked.
- Axes: Perf = CPU/memory/allocations, Battery = wakeups/network/foreground work,
  Response = UI responsiveness / tap-to-result latency.

## Executive order

1. P0 Free configs deep verify and search loop.
2. P0 Applications tab package/icon load, search, and selection persistence.
3. P0 Connect path: `MainActivity.StartTunnelService` + service DNS-tunnel startup.
4. P1 Connected idle diagnostics timer and VPN service network callbacks.
5. P1 Subscription/server test flows at large list sizes.
6. P1 SharedPreferences write bursts and config import/export sync sections.

## P0 - high value targets

### 1. Free configs: deep verify spins libbox repeatedly

Code:

- `VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs:102-169` - starts Java deep verify via
  `Task.Run`.
- `VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs:194-235` - second probe starts another
  Java verify invocation / another transient BoxService.
- `VPNRouter.Android/AndroidDeepVerifyBox.java:75-164` - per candidate creates and starts
  `BoxService`, waits for SOCKS, probes HTTP, closes.
- `VPNRouter.Android/AndroidDeepVerifyBox.java:501-519` - enumerates `AndroidCAStore` on every
  verify box; unlike `VpnRouterService.systemCertificates`, this path has no static PEM cache.
- `VPNRouter.Android/AndroidDeepVerifyBox.java:566-569` - logs every libbox line.

Why marked:

- This is probably the heaviest Android-only feature. Each verified candidate can spin up
  libbox once or twice. The second probe is quality-positive, but the comment itself notes
  it doubles libbox spin-up cost.
- Re-reading the Android certificate store for every verify instance is avoidable CPU/IO.
- Under a bad pool or high target count, this can become a battery-heavy search session.

What to measure:

- Time per candidate: primary-only fail, primary pass + secondary pass, primary pass +
  secondary fail.
- CPU while searching 10, 30, 50 candidates.
- Battery/wakelock behavior during a 10-minute free-config search.
- Logcat line rate from `VpnRouter.DV` / libbox while verifying.

Candidate fixes to evaluate later:

- Cache system certificate PEMs in `AndroidDeepVerifyBox` the same way `VpnRouterService`
  caches them.
- Combine primary and secondary probe into a single Java bridge invocation and one BoxService
  lifetime.
- Add a hard verified-candidate / tested-candidate cap per run.
- Throttle or aggregate deep-verify progress/status UI updates.

### 2. Free configs: pool loop and UI event pressure

Code:

- `VPNRouter.Android/AndroidFreeConfigsOrchestrator.cs:148-152` - `FindAsync(... batchSize = 200)`.
- `VPNRouter.Android/AndroidFreeConfigsOrchestrator.cs:211-226` - loops batches until target
  or queue exhaustion.
- `VPNRouter.Android/AndroidFreeConfigsOrchestrator.cs:254-255` - `OnFound` per candidate.
- `VPNRouter.Android/AndroidApp.FreeConfigs.cs:1202-1224` - dispatches every status/progress
  update to UI thread.
- `VPNRouter.Android/AndroidApp.FreeConfigs.cs:1226-1244` - per found entry scans the
  ObservableCollection for dedupe, then adds.
- `VPNRouter.Android/AndroidApp.FreeConfigs.cs:1266-1320` - per upgraded entry scans search
  and saved lists, replaces rows, updates connect gate.

Why marked:

- The orchestration is backgrounded, but UI receives many small updates. On slower Android
  devices this can feel like a sticky overlay even if the actual testing thread is fine.
- Dedupe via linear scan is fine for tens of rows, but starts to matter if the search
  results list grows and the event rate is high.

What to measure:

- UI frame time while search is running and user scrolls/taps.
- Dispatcher queue lag with target=10, target=30, target=50.
- Result count where UI event pressure becomes visible.

Candidate fixes to evaluate later:

- Coalesce progress/status to at most 4-5 UI updates per second.
- Maintain a `HashSet<string>` for displayed free-config IDs.
- Batch collection additions/replacements where Avalonia list rendering allows it.

### 3. Applications tab: PackageManager scan + eager icon conversion

Code:

- `VPNRouter.Android/AppListLoader.cs:80-224` - `GetInstalledApplications(MatchAll)`,
  `QueryIntentActivities(MatchAll)`, labels, icon loading, sorting.
- `VPNRouter.Android/AppListLoader.cs:192-198` - local comment already calls out cold icon
  conversion cost: about 10 ms x 100 apps on KYOCERA.
- `VPNRouter.Android/AppIconCache.cs:68-121` - cache miss converts drawable outside lock.
- `VPNRouter.Android/AppIconCache.cs:150-179` - drawable -> Android bitmap -> PNG stream ->
  Avalonia bitmap.
- `VPNRouter.Android/AndroidApp.axaml.cs:3567-3628` - reseeds the app picker when the
  Applications tab opens.
- `VPNRouter.Android/AndroidApp.axaml.cs:3699-3724` - show-system toggle reloads package list.
- `VPNRouter.Android/AndroidApp.axaml.cs:3933-3944` - each row renders cached bitmap with
  high-quality interpolation.

Why marked:

- Loading is already off the UI thread, which is good. The cost is still real: package manager
  scans, labels, and icon rasterization are CPU/alloc-heavy and happen at tab-open time.
- The icon path does a PNG roundtrip; this is simple and portable, but it creates transient
  allocations and CPU work.
- High-quality interpolation for many small icons may add scroll cost.

What to measure:

- Cold Applications tab open time after app start.
- Warm tab open time after cache is populated.
- Show system apps toggle time.
- Memory delta after opening Applications with 100, 300, 700 installed packages.
- Scroll jank in the app picker list.

Candidate fixes to evaluate later:

- Lazy icon loading for visible rows first; load labels/package list before icons.
- Reduce icon size to 64 or 48 px if visual quality remains acceptable.
- Avoid PNG roundtrip if a direct pixel path to Avalonia bitmap is available.
- Cache app list snapshot for the session and invalidate on package change broadcasts instead
  of reseeding on every tab activation.

### 4. Applications tab: search/filter and per-tap persistence

Code:

- `VPNRouter.Android/AndroidApp.axaml.cs:3727-3744` - `OnAppPickerSearchChanged` calls
  `ApplyAppPickerFilter()` for every text change.
- `VPNRouter.Android/AndroidApp.axaml.cs:3744+` - filter rebuilds visible app rows from
  `_appPickerCache`.
- `VPNRouter.Android/AndroidApp.axaml.cs:3903` - every package checkbox tap writes
  `AndroidStorage.SetPerAppPackages(_appPickerSelected)`.
- `VPNRouter.Android/AndroidApp.axaml.cs:3925` - custom category checkbox tap can also write
  `AndroidStorage.SetCustomCategories(_advAppsCustomCategories)`.
- `VPNRouter.Android/AndroidApp.axaml.cs:3930` - each tap recalculates category counts.
- `VPNRouter.Android/AndroidApp.axaml.cs:4477-4526` - category count calculation scans
  selected apps and category definitions.

Why marked:

- Text search has no debounce. Typing a multi-character query over a large app list can rebuild
  the UI on every keystroke.
- `SharedPreferences.Apply()` is async, so this is not a hard UI-blocking disk write, but
  every tap still serializes the package list and schedules disk work.
- For category editing, a single tap can cause two JSON serializations and count refresh.

What to measure:

- Typing latency for 10 characters with 100, 300, 700 app entries.
- 50 checkbox taps in a row: UI responsiveness, write count, CPU, GC.
- Search while icons are still loading.

Candidate fixes to evaluate later:

- Debounce search by 150-250 ms.
- Batch per-app package persistence: write after idle/debounce or when leaving the tab.
- Keep category counts incremental for checkbox taps.

### 5. Connect path: synchronous Activity-side config resolution/build

Code:

- `VPNRouter.Android/MainActivity.cs:917-1150` - `StartTunnelService()` resolves mode, reads
  storage, builds sing-box JSON, optionally writes debug dump.
- `VPNRouter.Android/MainActivity.cs:963-981` - custom JSON path reads and injects config.
- `VPNRouter.Android/MainActivity.cs:1076-1114` - subscription/manual path calls
  `AndroidStorage.GetActiveServer()` and `AndroidConfigBuilder.BuildConfigJson(...)`.
- `VPNRouter.Android/MainActivity.cs:1164-1222` - reads per-app packages, puts config/per-app
  arrays into intent extras, starts foreground service.
- `VPNRouter.Android/AndroidStorage.cs:591-670` - `GetActiveServer()` reads servers and
  subscriptions and resolves selected server.
- `VPNRouter.Android/AndroidStorage.cs:770-796` - per-app package list JSON deserialize/serialize.

Why marked:

- This path runs in the Activity side immediately after the VPN permission flow / connect tap.
  Large subscription lists, large per-app lists, or custom configs can stretch tap-to-service
  dispatch time.
- Passing a large config JSON and package array through intent extras is probably fine at
  current sizes, but should be tested against worst-case data.

What to measure:

- Tap Connect -> `StartForegroundService` call.
- Tap Connect -> foreground notification.
- Tap Connect -> `TUNNEL_UP`.
- Same measurements with 1, 100, 1000 servers and 0, 50, 300 per-app packages.
- Custom config size threshold where UI responsiveness degrades.

Candidate fixes to evaluate later:

- Move config resolution/build off the UI path before service dispatch, with a visible
  "preparing" state.
- Cache active server resolution if subscriptions/server list have not changed.
- Consider file handoff for very large generated config instead of large intent extras.

### 6. DNS-tunnel connect startup: local port wait and resolver work

Code:

- `VPNRouter.Android/VpnRouterService.java:945-950` - starts Slipstream then waits for local
  DNS-tunnel port.
- `VPNRouter.Android/VpnRouterService.java:1001-1032` - `waitForLocalPort` uses a dedicated
  `slipstream-portcheck` thread, timeout 8s, socket connect every 200 ms.
- `VPNRouter.Android/VpnRouterService.java:963-990` - `readSystemResolvers()` walks networks
  and DNS servers.

Why marked:

- DNS-tunnel startup adds a serial wait before libbox start. If Slipstream is slow or broken,
  connect latency can jump up to the 8s timeout.
- The polling thread is short-lived, but repeated connect attempts under bad DNS-tunnel
  conditions can burn time and battery.

What to measure:

- DNS-tunnel tap-to-connected under normal network.
- DNS-tunnel tap-to-error when local front cannot bind.
- Reconnect loop behavior under flaky network.

Candidate fixes to evaluate later:

- Replace fixed polling with a direct readiness signal from Slipstream if possible.
- Record fine-grained timestamps in logs for `startSlipstream`, `waitForLocalPort`,
  `boxService.start`.

## P1 - important conditional targets

### 7. Connected idle: 1 Hz diagnostics UI timer

Code:

- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs:429-440` - starts a 1 Hz `DispatcherTimer`.
- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs:452-530` - every tick updates uptime and every
  30s runs health probe.
- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs:545-608` - health probe checks `singbox.log`
  mtime/size and `MainActivity.IsVpnTransportActive(ctx)`.
- `VPNRouter.Android/MainActivity.cs:446-466` - `IsVpnTransportActive` enumerates networks.

Why marked:

- The implementation already avoids writing identical text and skips some advanced footer work
  when collapsed. Still, a 1 Hz UI wakeup while connected is a battery surface, especially if
  the app stays foregrounded for long periods.
- Health probes do light IO and ConnectivityManager calls every 30s.

What to measure:

- 15-minute connected idle, screen on, simple mode visible.
- 15-minute connected idle, advanced shell open.
- 30-minute connected idle, app backgrounded if timer is still alive.
- Battery stats: wakeups, CPU time, network callback count.

Candidate fixes to evaluate later:

- Pause or slow the UI uptime timer when the Activity is not visible.
- Keep service-level health separate from visible UI refresh.
- Use a 5s visible timer after the first minute if second-level uptime is not needed.

### 8. VPN service network callbacks and auto-reconnect work

Code:

- `VPNRouter.Android/VpnRouterService.java:226-237` - dedicated `vpn-net-monitor`
  `HandlerThread`.
- `VPNRouter.Android/VpnRouterService.java:1628-1731` - registers default network callback.
- `VPNRouter.Android/VpnRouterService.java:1733-1785` - `fireUpdate()` reads SharedPreferences,
  resolves default interface, may retry 10 x 50 ms, calls `listener.updateDefaultInterface`.
- `VPNRouter.Android/VpnRouterService.java:1787-1835` - auto-reconnect on network changed.

Why marked:

- Running on a HandlerThread is good. The risk is event storms: bad Wi-Fi/cell handoff can
  trigger many callbacks and retries.
- Each callback reads preferences and may log/update libbox. This is probably fine on stable
  networks, but worth measuring in handoff scenarios.

What to measure:

- Wi-Fi off/on while connected.
- Move between Wi-Fi and cellular.
- Airplane mode on/off.
- Auto-reconnect on and off.
- Count callback invocations, reconnect attempts, and CPU spikes.

Candidate fixes to evaluate later:

- Debounce `fireUpdate()` interface updates.
- Cache auto-reconnect preference while service is running and update it on explicit setting
  changes.
- Rate-limit repeated reconnect attempts beyond current safeguards if handoff storms appear.

### 9. Subscriptions: refresh all has unbounded concurrency

Code:

- `VPNRouter.Android/AndroidApp.SubscribePage.cs:853-860` - reseeds subscriptions on tab open.
- `VPNRouter.Android/AndroidApp.SubscribePage.cs:1239-1261` - refresh one subscription.
- `VPNRouter.Android/AndroidApp.SubscribePage.cs:1264-1298` - refresh all enabled subscriptions
  with `Task.WhenAll`.
- `VPNRouter.Android/AndroidApp.SubscribePage.cs:1301-1315` - transient status delay has no CTS.

Why marked:

- Refresh-all fans out all enabled subscriptions at once. With a few subscriptions this is fine;
  with many, it can create a network/CPU/battery burst.
- No per-run cancellation path is obvious from this code.

What to measure:

- Refresh all with 1, 5, 20 subscriptions.
- Timeout/error behavior when several sources hang.
- UI responsiveness while refresh-all is running.

Candidate fixes to evaluate later:

- Cap concurrent subscription refreshes to 2-3.
- Add cancellation / stop refresh.
- Coalesce result list rebuilds and status banners.

### 10. Server testing: large-list rebuild and result persistence

Code:

- `VPNRouter.Android/AndroidApp.ServerList.cs:18-35` - per-subscription server test concurrency
  is capped at 4.
- `VPNRouter.Android/AndroidApp.ServerList.cs:112-118` - list rebuild coalescing exists.
- `VPNRouter.Android/AndroidApp.ServerList.cs:1340-1417` - Test All marks all rows and probes
  with concurrency 4, final result persistence.
- `VPNRouter.Android/AndroidApp.ServerList.cs:1420-1441` - single server test rebuilds list,
  writes results, rebuilds again.
- `VPNRouter.Android/AndroidStorage.cs:1182-1218` - server test results dictionary is pruned
  and serialized into SharedPreferences.

Why marked:

- Batch testing is reasonably designed: concurrency capped and final persistence batched.
- Single-row testing on a huge list can still rebuild the whole list twice and serialize
  results immediately.

What to measure:

- Test all with 100, 500, 1000 servers.
- Single test latency on 1000-server list.
- Sort toggles after test results are populated.

Candidate fixes to evaluate later:

- Row-level update for single test if Avalonia list plumbing permits.
- Debounce single-test result persistence.
- Persist large server-test results outside SharedPreferences if size grows.

### 11. SharedPreferences JSON write bursts

Code:

- `VPNRouter.Android/AndroidStorage.cs:1244-1268` - string writes use `Apply()`.
- `VPNRouter.Android/AndroidStorage.cs:1285-1333` - bool/int writes use `Apply()`.
- `VPNRouter.Android/AndroidStorage.cs:526-557` - server list JSON.
- `VPNRouter.Android/AndroidStorage.cs:770-796` - per-app package list JSON.
- `VPNRouter.Android/AndroidStorage.cs:1094-1119` - custom categories JSON.
- `VPNRouter.Android/AndroidStorage.cs:1182-1218` - server test results JSON.

Why marked:

- `Apply()` is the right default for UI preferences because it does not block waiting for disk.
  The remaining cost is serialization, allocations, and many scheduled writes during bursty UI
  interactions.

What to measure:

- GC count / allocation spikes during app picker checkbox bursts.
- Disk/writeback behavior after repeated settings changes.
- Size of servers/subscriptions/per-app/test-results JSON at realistic and worst-case levels.

Candidate fixes to evaluate later:

- Batch high-frequency writes: per-app packages, active category/tab, server-test results.
- Move larger blobs to app files if SharedPreferences file size becomes a startup or write
  problem.

### 12. Auto-update: launch check and progress chatter

Code:

- `VPNRouter.Android/AndroidApp.axaml.cs:535-551` - fire-and-forget update check on app launch.
- `VPNRouter.Android/AndroidApp.AutoUpdate.cs:189-223` - `RunUpdateCheckAsync`, guarded by
  `_updateInFlight`.
- `VPNRouter.Android/AndroidApp.AutoUpdate.cs:311-367` - download/install flow, UI progress.
- `VPNRouter.Android/AndroidUpdater.cs:114-130` - reports download percent on every buffer read.

Why marked:

- The check is async and should not block launch, but it is still network work on every launch
  unless throttled by the update source.
- APK download progress can post many UI updates if percent reports repeat or arrive too fast.

What to measure:

- Cold launch with network unavailable / captive / slow.
- Launch count per day and update-check request count.
- APK download UI update count for a large APK.

Candidate fixes to evaluate later:

- Throttle automatic update check by last-check timestamp.
- Progress UI should only update when visible percent changes, and at a reasonable max rate.

### 13. Config import/export: synchronous snapshot/apply around UI overlay

Code:

- `VPNRouter.Android/AndroidApp.ConfigShare.cs:440-480` - export click builds snapshot and
  serializes JSON before SAF save request.
- `VPNRouter.Android/AndroidApp.ConfigShare.cs:515-563` - imported payload is parsed and previewed
  on UI dispatcher.
- `VPNRouter.Android/AndroidApp.ConfigShare.cs:566-590` - apply snapshot is synchronous.
- `VPNRouter.Android/AndroidConfigShare.cs:48-111` - `BuildSnapshot` reads subscriptions and
  optional per-app packages.
- `VPNRouter.Android/AndroidConfigShare.cs:121-221` - `ApplySnapshot` writes multiple preference
  keys.
- `VPNRouter.Android/AndroidConfigShare.cs:231-245` - backup writes current state to file.

Why marked:

- User-triggered and not a constant drain, but large config/subscription/per-app payloads can
  make the overlay feel frozen.

What to measure:

- Export with 0/100/1000 servers and 0/300 per-app packages.
- Import same payload; parse preview time and apply time.

Candidate fixes to evaluate later:

- Run snapshot/parse/apply in background with explicit progress and disabled buttons.
- Keep preview short for very large files.

### 14. Advanced shell tab reseed / language rebuild

Code:

- `VPNRouter.Android/AndroidApp.AdvancedShell.cs:558-595` - tab switch persists active tab and
  reseeds active tab state.
- `VPNRouter.Android/AndroidApp.AdvancedShell.cs:597-615` - lazy-builds tab content.
- `VPNRouter.Android/AndroidApp.AdvancedShell.cs:742-809` - language refresh removes cached tab
  content, clears cache, rebuilds current tab.
- `VPNRouter.Android/AndroidApp.AdvancedShell.cs:519-550` - shell close stops background work,
  reloads server list and summary.

Why marked:

- Lazy tab build is good. The risk is repeated reseed/persistence on tab switch and a heavy
  rebuild on language changes while advanced shell is open.

What to measure:

- First open of each advanced tab.
- Switching between Servers / Applications / Public / Subscribe after data is loaded.
- Language toggle while Applications/Public tab is active.

Candidate fixes to evaluate later:

- Avoid persisting active tab if unchanged.
- Defer heavy reseed until tab is visible and previous reseed is complete.
- For language refresh, update labels in-place for heavy tabs if full rebuild is costly.

## P2 - lower risk / latent hazards

### 15. Pulse animations while connecting

Code:

- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs:222-235` - pulse cancellation/disposal.
- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs:291-296` - stop pulsing.
- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs:361-400` - infinite Avalonia animation while
  chip is in connecting state.

Why marked:

- Previous CTS/timer leak appears fixed. Remaining risk is a stuck "connecting" state that
  keeps animation running and wakes UI.

What to measure:

- Failed connect paths, permission cancel, DNS-tunnel timeout, service crash.
- Confirm pulse stops in all terminal states.

### 16. Manual touch-scroll handler in simple mode

Code:

- `VPNRouter.Android/AndroidApp.axaml.cs:1625-1727` - custom pointer pressed/moved/released
  logic for Android ScrollViewer touch drag.

Why marked:

- This was added to make scrolling work reliably. It runs on every pointer move and can hide
  keyboard while dragging from a TextBox. Worth checking frame time during long scrolls.

What to measure:

- `dumpsys gfxinfo` frame stats while scrolling simple page.
- Scroll with keyboard visible and TextBox focused.

### 17. Wake-lock and task-removed restart policy

Code:

- `VPNRouter.Android/VpnRouterService.java:215-224` - 60s partial wake-lock for connect fail-safe.
- `VPNRouter.Android/VpnRouterService.java:778-809` - acquire/release connect wake-lock.
- `VPNRouter.Android/VpnRouterService.java:1160-1205` - `onTaskRemoved` schedules restart via
  AlarmManager if tunnel is active and app is battery-exempt.

Why marked:

- A bounded wake-lock is appropriate for tunnel startup. Need to ensure repeated failures do not
  keep the device in frequent wake/restart cycles.

What to measure:

- Failed connect loops: wake-lock held time and release path.
- OEM kill / swipe-away behavior with battery optimization exempt and not exempt.
- AlarmManager restart frequency.

### 18. Libbox log forwarding

Code:

- `VPNRouter.Android/VpnRouterService.java:1846-1849` - `writeLog(String message)` forwards
  every libbox line to Logcat.
- `VPNRouter.Android/AndroidDeepVerifyBox.java:566-569` - same pattern for deep verify.

Why marked:

- Info-level logging is valuable for support, but high log volume costs CPU/IO and can make
  profiling noisy. This is especially relevant during free-config deep verify and reconnect
  storms.

What to measure:

- Logcat lines/sec during steady connected idle.
- Logcat lines/sec during connect, reconnect, DNS-tunnel fail, deep verify.

Candidate fixes to evaluate later:

- Gate verbose forwarding behind debug or a diagnostics switch.
- Rate-limit repeated identical lines.

### 19. `AndroidSingBoxRuntime` sync wrappers

Code:

- `VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs:166-193` - `IsRunning()` calls async
  probe via `.GetAwaiter().GetResult()`.

Why marked:

- I did not find this as a hot UI call path during this pass. It is still a latent hazard:
  if any UI path starts calling the sync wrapper, it can block up to the HTTP timeout.

What to measure:

- Search call sites before use.
- Add a rule: UI code should use async path only.

## Existing good mitigations not to lose

- `VpnRouterService` lifecycle work is moved off main thread:
  `VPNRouter.Android/VpnRouterService.java:239-279`.
- Network callbacks use a dedicated HandlerThread:
  `VPNRouter.Android/VpnRouterService.java:226-237`.
- Main service caches system certificates:
  `VPNRouter.Android/VpnRouterService.java:1577-1606`.
- Diagnostics export is offloaded with `Task.Run`:
  `VPNRouter.Android/AndroidApp.Notifications.cs:230-251`.
- Server Test All caps concurrency at 4 and coalesces list rebuilds:
  `VPNRouter.Android/AndroidApp.ServerList.cs:18-35`, `:112-118`.
- SharedPreferences writes use `Apply()` instead of blocking `Commit()` for ordinary UI prefs:
  `VPNRouter.Android/AndroidStorage.cs:1244-1333`.
- App list loading is backgrounded before UI update:
  `VPNRouter.Android/AndroidApp.axaml.cs:3567-3628`.

## Measurement checklist

### Baseline commands

Use package name `com.ninitux.vpnrouter`.

```bash
adb shell dumpsys meminfo com.ninitux.vpnrouter
adb shell ps -T -A | grep com.ninitux.vpnrouter
adb shell dumpsys gfxinfo com.ninitux.vpnrouter framestats
adb shell dumpsys batterystats --reset
adb shell dumpsys batterystats com.ninitux.vpnrouter
adb logcat -c
adb logcat -v time | grep -E "VpnRouter|Libbox|VpnRouter.DV"
```

If available on the device:

```bash
adb shell top -H -p $(adb shell pidof com.ninitux.vpnrouter)
adb shell simpleperf stat -p $(adb shell pidof com.ninitux.vpnrouter) --duration 30
```

### Scenarios

1. Cold launch:
   - clear app from recents, launch;
   - record first frame, time until main UI interactive, update-check behavior;
   - repeat with network offline and slow network.

2. Connected idle:
   - connect normal VLESS;
   - keep screen on 15 minutes on simple page;
   - repeat with advanced shell open;
   - repeat app backgrounded 30 minutes if service remains active.

3. Connect latency:
   - 30 connect/disconnect cycles normal VLESS;
   - 30 cycles DNS-tunnel;
   - repeat with large per-app package list and large subscription list.

4. Applications tab:
   - first open after cold launch;
   - warm reopen;
   - toggle Show system apps;
   - type 10-char search;
   - tap 50 package checkboxes;
   - scroll top to bottom while recording `gfxinfo`.

5. Free configs:
   - find target 10, 30, 50;
   - bad network / high failure pool;
   - cancel mid-run;
   - measure CPU, log rate, UI responsiveness, RSS, thread count.

6. Subscriptions:
   - refresh all with 1, 5, 20 enabled subscriptions;
   - one hanging source;
   - while running, switch tabs and scroll.

7. Server tests:
   - Test All with 100, 500, 1000 servers;
   - single test on a 1000-server list;
   - sort by latency after results.

8. Network handoff:
   - Wi-Fi off/on;
   - cellular/wifi switch;
   - airplane mode on/off;
   - auto-reconnect enabled/disabled.

9. Import/export:
   - export/import config with large subscriptions and per-app package list;
   - check UI freeze and total operation time.

10. Update download:
    - manual update check;
    - APK download on slow network;
    - count progress UI updates.

## Proposed walkthrough order

### Pass A - quick profiling without code changes

- Capture baseline from `plans/android-deep-qa-perf-2026-06-15.md` again on the same device.
- Run scenarios 2, 3, 4, and 5 first; these are most likely to produce visible data.
- Collect logs with timestamps around connect and DNS-tunnel startup.

### Pass B - cheap wins if measurements confirm

- Cache `AndroidDeepVerifyBox.systemCertificates()`.
- Debounce app picker search and per-app package persistence.
- Throttle free-config progress/status UI updates.
- Throttle auto-update download progress and automatic launch check.

### Pass C - structural fixes only if needed

- Move connect config build off Activity/UI path.
- Rework app picker icon loading to lazy visible-row loading.
- Coalesce subscription refresh-all with a concurrency limiter and cancellation.
- Replace DNS-tunnel port polling with a readiness callback if Slipstream can expose one.

## Open questions

- What is the realistic upper bound for installed apps on target user devices?
- What is the realistic upper bound for servers after subscription aggregation?
- Do we want "battery saver mode" for Android UI, where visible timers/progress refreshes are
  intentionally slower while the tunnel itself remains unchanged?
- Should free-config deep verify prioritize fewer but higher-quality candidates on phones,
  compared with desktop?
