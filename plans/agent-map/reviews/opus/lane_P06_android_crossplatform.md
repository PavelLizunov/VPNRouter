# Adversarial Review: P06 — Android Architecture, JNI Libbox Bridge & Cross-Platform Edge Cases

> **TL;DR:** Deep adversarial audit of the Android VPN service (`VpnRouterService.java`), JNI bridge layer (`SlipstreamNative.java`, `AndroidDeepVerifyBox.java`), C# UI lifecycle (`AndroidApp.VpnLifecycle.cs`), external automation (`VpnControlReceiver.cs`), and their cross-platform counterparts (`VPNRouterService.cs`). Identified **14 actionable findings**: 3 P0 (traffic fail-open, process crash, dual-init race), 6 P1 (state desynchronization, health false positives, blocking handlers, header injection, FGS restriction, unprotected receiver), and 5 P2 (thread interrupt suppression, iterator fragility, executor churn, unbounded read, stale VPN transport signal).

---

## 1. Subsystem State Machines

### 1.1 Android Native Tunnel Lifecycle (Implemented)

The actual implemented state machine in `VpnRouterService.java`:

```
                  ┌─ ACTION_START (extras) ───────────────────────┐
                  │                                               │
 [Idle/Destroyed] ──── ACTION_RESTART / null / AlwaysOn ─────────►[ensureForegroundStarted]
                  │                                               │
                  │                                       fail: broadcast ERROR + stopSelf
                  │                                               │ success
                  │                                               ▼
                  │                                    [submitLifecycle(startTunnel)]
                  │                                               │
                  │                                    boxService != null? ──YES──► teardownTunnelResources()
                  │                                               │ NO                    │
                  │                                               ◄────────────────────────┘
                  │                                               │
                  │                                    acquireWakeLock → ensureLibboxSetup
                  │                                    → startSlipstream → startLibboxService
                  │                                    → persistLastGood → broadcast UP
                  │                                    → setTunnelLive(true) → startStatsPoller
                  │                                               │
                  │                                               ▼
                  │                                         [TUNNEL LIVE]
                  │                                               │
                  │              ACTION_STOP / onRevoke ──────────┤
                  │              onDestroy ────────────────────────┤
                  │              onTaskRemoved (schedules alarm) ──┤
                  │                                               │
                  │                                    [submitLifecycle(stopTunnel)]
                  │                                    teardownTunnelResources
                  │                                    stopForeground + broadcast DOWN
                  │                                    setTunnelLive(false)
                  │                                               │
                  └───────────────────────────────────────────────┘
```

### 1.2 C# UI State Machine (Implemented in `AndroidApp.VpnLifecycle.cs`)

```
  [Off] ──── OnConnectClicked ────► [Connecting]   (chip pulse, SetVpnChipState)
                                         │
                            IntentChanged(true) from broadcast
                                         │
                                         ▼
                                      [On]         (chip green, diagnostics timer)
                                         │
                            IntentChanged(false) / TUNNEL_ERROR
                                         │
                                         ▼
                                      [Off]        (chip off, timer may persist for error display)
```

### 1.3 Critical State Machine Gaps

**Gap 1: Missing "Error" state in UI.** `UpdateConnectionState(bool)` is binary — connected/not. There is no distinct "Error" or "Failed" chip state. When `TUNNEL_ERROR` fires before `IntentChanged(false)`, the UI briefly shows Connecting→Off with a transient error banner. But if `IntentChanged(false)` is never delivered (broadcast lost), the chip stays in `Connecting` indefinitely — an unreachable-by-timeout limbo state.

**Gap 2: No timeout on Connecting state.** Once `SetVpnChipState(ChipState.Connecting)` fires (line 109), there is no fallback timer to transition to Off if no broadcast arrives within N seconds. If `VpnRouterService.startTunnel()` hangs in `startSlipstreamIfNeeded()` (e.g. `waitForLocalPort` blocks for 8s but the lifecycle executor queue has a prior stalled task), the UI pulse-animates indefinitely.

**Gap 3: `onDestroy` enqueues `stopTunnel` after `lifecycleExecutor.shutdown()`.** The `submitLifecycle` in `onDestroy` (line 1358) enqueues before `shutdown()` (line 1367). But if the executor queue already has a stalled `startTunnel`, the enqueued `stopTunnel` blocks behind it. `lifecycleExecutor.shutdown()` prevents new submissions but does NOT cancel in-flight work. If `startTunnel` is blocked in `waitForLocalPort(8s)` + `boxService.start()`, the `stopTunnel` waits an unbounded time. The `onDestroy` call returns immediately (main thread), so the process may be killed before `stopTunnel` runs.

---

## 2. Concurrency Hazards & Race Conditions

### 2.1 Dual `libboxSetupDone` Flags — Double Initialization Race

`VpnRouterService.java` has:
```java
private static boolean libboxSetupDone = false;  // line 205
private synchronized void ensureLibboxSetup() { if (libboxSetupDone) return; ... }
```

`AndroidDeepVerifyBox.java` has:
```java
private static final AtomicBoolean libboxSetupDone = new AtomicBoolean(false); // line 71
```

These are **two separate static flags in two separate classes**. If Deep Verify runs before (or concurrently with) the tunnel service, `Libbox.setup()` is called twice with potentially different directory arguments (DeepVerifyBox uses `ctx.getFilesDir()` directly; the service uses `getFilesDir()` + a `data/` subdirectory as working path). Whether the Go runtime tolerates double-init is an implementation detail of `libbox.aar` — a silent corruption vector.

### 2.2 `VpnRouterService.libboxSetupDone` Is Not Volatile

The flag `private static boolean libboxSetupDone` is read inside `synchronized void ensureLibboxSetup()`. The `synchronized` keyword on the method synchronizes on `this` (the service instance). Since `libboxSetupDone` is **static** (class-level), and synchronization is on an **instance**, two different instances of VpnRouterService (theoretically possible if Android recreates the service via `START_STICKY` while the old instance hasn't been GC'd) could race on the read. In practice Android serializes service lifecycle, but the JMM does not guarantee visibility of a non-volatile static across different monitor locks.

### 2.3 Stats Poller Race with Tunnel Teardown

`pollStatsOnce()` reads `clashApiSecret` (volatile, safe) but then opens a socket and makes an HTTP request. Meanwhile, `teardownTunnelResources()` calls `stopStatsPoller()` which does `shutdownNow()`. The `shutdownNow()` interrupts the poller thread, but the socket connect/read operations in `pollStatsOnce` are NOT interruptible (Java NIO would be, but `java.net.Socket` blocks). The poller thread will finish its current tick even after `shutdownNow()`. This is benign (the socket will time out or succeed), but the broadcast `sendBroadcast(ACTION_STATS)` can fire AFTER the tunnel is torn down, delivering stale stats to the UI.

### 2.4 Cross-Language State Triple-Source Desynchronization

Three independent "is connected" truth sources exist:

| Source | Location | Written by | Read by |
|--------|----------|-----------|---------|
| `_intendedConnected` | C# static in `MainActivity` | `SetIntent()` on UI thread | UI chip/card state |
| `tunnel_live` | Java SharedPreferences | `setTunnelLive()` on lifecycle worker | `OnResume` re-sync, `VpnControlReceiver.TOGGLE` |
| `boxService != null` | Java volatile field | `startTunnel`/`teardownTunnelResources` on lifecycle worker | `onStartCommand` guard, `onTaskRemoved` |

**Desync scenario:** Tunnel crashes in the Go runtime (libbox panic). `boxService` becomes invalid but is never nulled (Go panic doesn't trigger Java `finally`). `tunnel_live` stays `true`. `_intendedConnected` stays `true`. All three sources agree the tunnel is "up" while no traffic routes. The health probe's `TRANSPORT_VPN` check may still return `true` (kernel VPN interface persists until `pfd.close()`), masking the failure for up to 60 seconds.

---

## 3. Fail-Closed & Leak Invariants

### 3.1 Per-App Include Mode: Traffic Fail-Open (Self-Inclusion Routing Loop)

In `openTun()` (line 1548), when `isInclude` is true:

```java
if (isInclude) {
    addPackages(builder, options.getIncludePackage(), true);
    // ... adds pendingPerAppPackages as allowed ...
    // NOTE: NO addDisallowedApplication(getPackageName())
}
```

Android's VPN include mode means "only listed apps route through VPN." If VPNRouter's own package (`com.ninitux.vpnrouter`) appears in `pendingPerAppPackages` (populated from the C# layer) or in `options.getIncludePackage()` (from libbox's parsed config), **all sing-box outbound proxy traffic routes into tun0**, creating an immediate infinite routing loop → device-wide network death.

The defense is in the C# `AppListLoader.cs` (filters the own package), but:
- The Always-on/boot/swipe-recovery restore path (`loadLastGoodConfig()`) reads `pendingPerAppPackages` from SharedPreferences, **bypassing the C# filter entirely**
- A crafted `SharedPreferences` entry (via `adb backup`/`adb restore` or root) can inject the package name
- The Java service has **no independent guard** against self-inclusion

### 3.2 VPN Transport Check False Positive

`IsVpnTransportActive()` (MainActivity.cs:616) checks whether **any** VPN transport is active on the device — not specifically VPNRouter's. If another VPN app is active, or if Android hasn't torn down a stale VPN transport from a crashed service, the health probe and resume re-sync treat the tunnel as alive when it's dead. Traffic would fail-open through the underlying network without VPN protection.

### 3.3 Crash-Path Wake Lock Leak

`acquireConnectWakeLock()` creates a new `WakeLock` each call (line 873). If `connectWakeLock` is non-null and held when `acquireConnectWakeLock` is called again (e.g., rapid successive `startTunnel` calls via the lifecycle executor), the old lock is overwritten without release. The 60-second timeout is the backstop, but for 60 seconds the CPU is held awake by the orphaned lock. `setReferenceCounted(false)` prevents *double-acquire* leaks on the same lock, but doesn't prevent orphaning via field overwrite.

Actually, looking more closely: line 872 checks `if (connectWakeLock != null && connectWakeLock.isHeld()) return;` — so if the old lock is held, it returns without creating a new one. And `teardownTunnelResources` at the start of `startTunnel` calls `releaseConnectWakeLock`. So this is mitigated but fragile: the release-then-acquire window between teardown's release and the new acquire is unguarded.

---

## 4. Security & Privilege Boundaries

### 4.1 `VpnControlReceiver` Exported Without Caller Verification

```csharp
[BroadcastReceiver(Name = "com.ninitux.vpnrouter.VpnControlReceiver",
    Exported = true, Enabled = true)]
```

Once the user enables external control (`AndroidStorage.GetExternalControlEnabled()`), **any app** on the device can send `EXT_STOP` to kill the VPN tunnel — no sender signature, UID, or permission check. A malicious app could:
1. Send `EXT_STOP` to silently disable VPN protection
2. Send `EXT_TOGGLE` rapidly to create a denial-of-service on the tunnel lifecycle
3. Send `EXT_START` to force a tunnel restart (consuming battery, disrupting connections)

The opt-in gate prevents zero-config abuse, but the documentation presents this as "exactly like granting a dangerous permission" — it is not. Dangerous permissions have OS-enforced caller identity. This receiver has none.

### 4.2 Clash API Secret Whitespace / HTTP Header Injection

`extractClashApiSecret` (line 1174) returns the raw string from `ca.optString("secret", "")` without `.trim()`. In `pollStatsOnce` (line 1228), the secret is interpolated directly into an HTTP header:

```java
String auth = "Authorization: Bearer " + secret + "\r\n";
os.write(("GET /connections HTTP/1.0\r\nHost: 127.0.0.1\r\n" + auth + "\r\n").getBytes("UTF-8"));
```

If the config JSON contains `"secret": "tok\r\nX-Injected: evil"`, this injects arbitrary HTTP headers into the Clash API request. The practical impact is limited (loopback only), but violates HTTP protocol correctness and could confuse a future Clash API proxy.

### 4.3 SharedPreferences Cross-Language Trust Boundary

The `vpnrouter_settings` SharedPreferences file is the critical trust boundary between Java (`VpnRouterService`) and C# (`AndroidStorage`). Both sides independently read/write keys like `last_good_config_json`, `tunnel_live`, and `last_good_per_app_packages_lines`. There is no schema versioning, no integrity check, and no locking between the Java `apply()` writes and C# reads. A corrupt or truncated SharedPreferences XML (e.g., from an abrupt process kill during `apply()`) could cause:
- `loadLastGoodConfig()` to parse a partial JSON string → `Libbox.checkConfig` throws → Always-on boot fails silently
- Per-app packages deserialized from a truncated newline-packed string → wrong apps routed through VPN

---

## 5. Inconsistencies & Protocol Violations

### 5.1 Windows Service vs Android Service: Unshared State Machine

| Aspect | Windows (`VPNRouterService.cs`) | Android (`VpnRouterService.java`) |
|--------|----|-----|
| Lifecycle model | `BackgroundService.ExecuteAsync` + `StopAsync` | `onStartCommand` + Intent dispatch |
| Concurrency | `Task.Run` + `TaskCompletionSource` | `ExecutorService` + `volatile` + `synchronized` |
| Config source | `config.yaml` file watcher (2s debounce) | Intent extras + SharedPreferences |
| Hot reload | `VpnEngine.ApplyAsync` (Clash API or process restart) | Full tunnel teardown + restart |
| Multi-engine | VPN + Zapret + TgProxy (3 independent engines) | VPN only (Zapret is in-tunnel, no TgProxy) |
| Ownership lock | `TunOwnershipLock` global semaphore | N/A (Android OS enforces single VPN) |
| Fail behavior | Watcher mode (passive file monitoring) | `stopSelf()` on failure |

**Key inconsistency:** Windows hot-reloads config changes without tunnel restart (Clash API patch). Android has no equivalent — any config change requires a full tunnel teardown and rebuild. A user switching between platforms would experience different config-change latency and interruption behavior.

### 5.2 Fire-and-Forget Task Race in Windows Service

Windows `VPNRouterService.cs` lines 107-113:
```csharp
if (settings.App.AutostartZapret)
    _ = AutostartZapretAsync(settings, stoppingToken);
if (settings.App.AutostartTgProxy)
    _ = AutostartTgProxyAsync(settings, stoppingToken);
```

These are unawaited. `StopAsync` waits for `_startupComplete` (VPN only), then calls `_zapret?.Stop()`. If Zapret is still in its `ResilientStarter` backoff loop, `_zapret` is null when `StopAsync` runs → the background task completes after `StopAsync` → orphaned `winws.exe` process. This is a Windows-specific defect with no Android equivalent but affects cross-platform behavioral parity.

### 5.3 `async void OnConnectClicked` Crash Vector

```csharp
private async void OnConnectClicked(object? sender, RoutedEventArgs e)  // line 57
{
    // ...
    await ApplyScannedSubscriptionUrlAsync(subscriptionUrl);  // line 98
```

`async void` propagates unhandled exceptions to the synchronization context → process crash. `ApplyScannedSubscriptionUrlAsync` performs network I/O (subscription refresh) and JSON parsing. Any unhandled exception (network timeout, malformed JSON, OOM during deserialization) terminates the application without user-visible error. This is the only `async void` in `AndroidApp.VpnLifecycle.cs`.

---

## 6. Prioritized Actionable Findings

---

### FINDING-P06_android_crossplatform-01
**Severity: P0 (traffic fail-open / routing loop)**
**Source Anchor:** `VpnRouterService.java:1548-1557`

**Mechanism:** In `openTun()` per-app include mode, `addDisallowedApplication(getPackageName())` is never called. Android's include mode implicitly excludes unlisted apps, but if VPNRouter's own package appears in `pendingPerAppPackages` (from `loadLastGoodConfig()` SharedPreferences restore, bypassing the C# `AppListLoader` filter) or in libbox's `getIncludePackage()` iterator, all outbound proxy traffic enters tun0 → infinite routing loop → device-wide network death.

**Threat Scenario:** User enables per-app include mode, connects successfully (C# filter removes own package). Config persisted to SharedPreferences. User modifies SharedPreferences via adb backup/restore with own package injected, or a future libbox config generator includes it. On Always-on boot, `loadLastGoodConfig()` loads the poisoned list → routing loop on boot, before any UI is visible.

**Fix:**
```java
if (isInclude) {
    addPackages(builder, options.getIncludePackage(), true);
    if (pendingPerAppPackages != null) {
        for (String pkg : pendingPerAppPackages) {
            if (pkg == null || pkg.isEmpty()) continue;
            if (pkg.equals(getPackageName())) continue;  // ← ADD THIS GUARD
            try { builder.addAllowedApplication(pkg); }
            catch (PackageManager.NameNotFoundException ignored) {}
        }
    }
}
```

---

### FINDING-P06_android_crossplatform-02
**Severity: P0 (application crash)**
**Source Anchor:** `AndroidApp.VpnLifecycle.cs:57`

**Mechanism:** `private async void OnConnectClicked(...)` awaits `ApplyScannedSubscriptionUrlAsync(subscriptionUrl)` which performs network I/O and JSON parsing. An unhandled exception in `async void` propagates to the synchronization context and terminates the process. No try/catch wraps the `await` call.

**Threat Scenario:** User enters a subscription URL, taps Connect. Subscription server returns malformed JSON or times out with an unexpected exception type. Application crashes immediately with no error message.

**Fix:**
```csharp
private async void OnConnectClicked(object? sender, RoutedEventArgs e)
{
    // ... existing code ...
    try
    {
        await ApplyScannedSubscriptionUrlAsync(subscriptionUrl);
    }
    catch (Exception ex)
    {
        OnTunnelErrorReported($"Subscription refresh failed: {ex.Message}");
    }
    return;
    // ... rest of method ...
}
```

---

### FINDING-P06_android_crossplatform-03
**Severity: P0 (double Go-runtime initialization)**
**Source Anchor:** `VpnRouterService.java:205,902` and `AndroidDeepVerifyBox.java:71,203`

**Mechanism:** Two independent `libboxSetupDone` flags exist — `VpnRouterService.libboxSetupDone` (plain `boolean`, `synchronized` on service instance) and `AndroidDeepVerifyBox.libboxSetupDone` (separate `AtomicBoolean`). If both code paths execute in the same process, `Libbox.setup()` is called twice with potentially different directory arguments (DeepVerifyBox uses `ctx.getFilesDir()` directly; the service creates an additional `data/` working subdirectory). Double-initialization behavior depends on the Go runtime — it may corrupt internal state, overwrite directory paths, or silently succeed. Neither guard sees the other's flag.

**Threat Scenario:** User taps "Verify" on a Free Config while the tunnel is starting. `AndroidDeepVerifyBox.verifyConfigSync` and `VpnRouterService.startTunnel` race — both call `Libbox.setup()` with different `SetupOptions`. Go runtime writes internal globals twice.

**Fix:** Unify into a single process-wide guard:
```java
// Shared utility class
public final class LibboxInit {
    private static final AtomicBoolean done = new AtomicBoolean(false);
    private static final Object lock = new Object();
    public static void ensure(Context ctx) throws Exception {
        if (done.get()) return;
        synchronized (lock) {
            if (done.get()) return;
            // ... single canonical setup with consistent paths ...
            done.set(true);
        }
    }
}
```

---

### FINDING-P06_android_crossplatform-04
**Severity: P1 (state desynchronization — stale "Connected" UI)**
**Source Anchor:** `MainActivity.cs:189,205` / `VpnRouterService.java:684,727` / `AndroidApp.VpnLifecycle.cs:672`

**Mechanism:** Three independent "is connected" truth sources (`_intendedConnected`, `tunnel_live` SharedPref, `boxService != null`) can desynchronize. If a Go runtime panic kills the libbox engine without triggering Java exception handling, `boxService` remains non-null, `tunnel_live` stays `true`, and `_intendedConnected` stays `true`. The health probe's `TRANSPORT_VPN` check (`IsVpnTransportActive`) returns `true` because the kernel VPN interface persists until `ParcelFileDescriptor.close()`.

**Threat Scenario:** libbox encounters a Go `panic()` (e.g., Reality handshake buffer overflow, DNS resolver nil map). The Go runtime calls `runtime.throw` which doesn't trigger Java exception handlers. The tunnel stops carrying traffic. All three state sources agree "connected." The user sees green status while traffic flows unencrypted through the underlying network.

**Fix:** Add a periodic liveness probe that checks whether `boxService` can still respond (e.g., a heartbeat ping to the Clash API), distinct from the file-based health probe. On failure, trigger `teardownTunnelResources()` and broadcast `ACTION_TUNNEL_DOWN`.

---

### FINDING-P06_android_crossplatform-05
**Severity: P1 (false health OK — masking dead tunnel)**
**Source Anchor:** `AndroidApp.VpnLifecycle.cs:672-675` / `MainActivity.cs:616-636`

**Mechanism:** `IsVpnTransportActive(Context)` checks whether **any** VPN transport is active on the device via `ConnectivityManager.GetAllNetworks()`. If another VPN app is running, or if Android hasn't torn down a stale transport from a crashed VPNRouter service, the health probe returns `true` and `_lastHealthOk` stays `true`. The fail-safe default (`return true` on any error) further biases toward false positives.

**Threat Scenario:** User installs a second VPN app (e.g., corporate MDM VPN). VPNRouter's sing-box crashes. Health probe sees the MDM VPN's `TRANSPORT_VPN` → green health → user believes VPNRouter is protecting traffic when it isn't.

**Fix:** Compare the VPN transport's underlying network interface name against the `tun` interface VPNRouter created (available from `currentPfd`'s associated `LinkProperties.getInterfaceName()`), or check sing-box's Clash API for a `200 OK` response as the liveness signal.

---

### FINDING-P06_android_crossplatform-06
**Severity: P1 (head-of-line blocking on network events)**
**Source Anchor:** `VpnRouterService.java:2014-2024`

**Mechanism:** `fireUpdate()` retries `NetworkInterface.getByName(name)` up to 10 times with `Thread.sleep(50)` — blocking the `netCallbackThread` (single-threaded `HandlerThread`) for up to 500ms. During this window, all queued `ConnectivityManager.NetworkCallback` events (interface drops, capability changes, new network available) are delayed. On a Wi-Fi↔cellular handoff, this can cause a 500ms blackhole where sing-box doesn't learn about the new default interface.

**Threat Scenario:** User walks out of Wi-Fi range. `onLost` fires for Wi-Fi. Simultaneously, `onAvailable` fires for cellular. The `onAvailable` handler enters `fireUpdate` and blocks in the retry loop. `onLost` queues behind it. sing-box never gets the "lost" signal until 500ms later, potentially after it's already timed out dials on the dead Wi-Fi interface.

**Fix:** Post the retry loop onto a separate worker thread or use a non-blocking exponential backoff via `Handler.postDelayed`:
```java
private void fireUpdateAsync(ConnectivityManager cm, Network network, int attempt) {
    // ... try getByName ...
    if (ni == null && attempt < 10) {
        service.ensureNetCallbackHandler().postDelayed(
            () -> fireUpdateAsync(cm, network, attempt + 1), 50);
        return;
    }
    // ... deliver update ...
}
```

---

### FINDING-P06_android_crossplatform-07
**Severity: P1 (HTTP header injection in Clash API polling)**
**Source Anchor:** `VpnRouterService.java:1174,1228`

**Mechanism:** `extractClashApiSecret()` returns the raw `secret` string from config JSON without calling `.trim()`. In `pollStatsOnce()`, it's interpolated directly into an HTTP header: `"Authorization: Bearer " + secret + "\r\n"`. A config with `"secret": "tok\r\nX-Injected: evil"` injects arbitrary headers. While the impact is limited (loopback to localhost:9090), it violates RFC 7230 and could cause misrouting if a future version proxies stats through a remote endpoint.

**Fix:**
```java
String s = ca.optString("secret", "").trim();
if (s.contains("\r") || s.contains("\n")) return null; // reject header-unsafe secrets
return s.isEmpty() ? null : s;
```

---

### FINDING-P06_android_crossplatform-08
**Severity: P1 (FGS restriction — silent automation failure)**
**Source Anchor:** `VpnControlReceiver.cs:92-93`

**Mechanism:** On Android 12+ (API 31+), `context.StartForegroundService(svc)` from a BroadcastReceiver when the app is in the background throws `ForegroundServiceStartNotAllowedException` unless the app is battery-optimization-exempt. The exception is caught (line 99), but the user's automation (Tasker/widget) silently fails. No feedback channel exists to inform the automation tool that the start was rejected.

**Threat Scenario:** User sets up Tasker automation to start VPN on Wi-Fi connect. Phone is in Doze. Tasker sends `EXT_START`. `StartForegroundService` throws. VPN doesn't start. User's traffic flows unprotected on the new Wi-Fi network. No notification, no error.

**Fix:** Add a result broadcast:
```csharp
catch (Exception ex)
{
    // Existing logging...
    var result = new Intent("com.ninitux.vpnrouter.EXT_RESULT")
        .SetPackage(context.PackageName)
        .PutExtra("success", false)
        .PutExtra("error", ex.Message);
    context.SendBroadcast(result);
}
```

---

### FINDING-P06_android_crossplatform-09
**Severity: P1 (exported receiver without caller authentication)**
**Source Anchor:** `VpnControlReceiver.cs:30`

**Mechanism:** `VpnControlReceiver` is `Exported = true` with no `android:permission` attribute. Once the user enables external control, **any** app on the device can send `EXT_STOP` to kill the VPN. There is no sender UID check, no signature verification, and no custom permission. The opt-in flag (`GetExternalControlEnabled`) is the sole gate. Unlike Android dangerous permissions (which the OS enforces per-caller), this is a global on/off switch that trusts all callers equally.

**Threat Scenario:** User enables external control for Tasker. A malicious app (or adware SDK) discovers the exported receiver via `PackageManager.queryBroadcastReceivers()` and sends `EXT_STOP` to disable VPN protection before exfiltrating data.

**Fix:** Define a custom signature-level permission for first-party use, and a separate runtime-checkable permission for third-party automation:
```xml
<permission android:name="com.ninitux.vpnrouter.CONTROL_VPN"
    android:protectionLevel="dangerous" />
<receiver ... android:permission="com.ninitux.vpnrouter.CONTROL_VPN">
```

---

### FINDING-P06_android_crossplatform-10
**Severity: P2 (suppressed thread interruption)**
**Source Anchor:** `AndroidDeepVerifyBox.java:243` / `VpnRouterService.java:2024`

**Mechanism:** `InterruptedException` is caught without re-interrupting the thread:
```java
// AndroidDeepVerifyBox.java:243
try { Thread.sleep(100); } catch (InterruptedException ignored) { return false; }

// VpnRouterService.java:2024
try { Thread.sleep(50); } catch (InterruptedException ignored) {}
```

The first swallows the interrupt and returns without restoring the interrupt flag. The second swallows it entirely. This violates the Java interruption contract: callers that interrupt these threads (e.g., `ExecutorService.shutdownNow()`) lose the ability to propagate cancellation upstream.

**Fix:** Add `Thread.currentThread().interrupt();` before the early return/continue. Note: `VpnRouterService.java:320` and `:1106` correctly restore the interrupt flag — these two locations are inconsistent with the rest of the file.

---

### FINDING-P06_android_crossplatform-11
**Severity: P2 (iterator consumption fragility)**
**Source Anchor:** `VpnRouterService.java:1573`

**Mechanism:** In the default/off mode branch of `openTun()`:
```java
boolean hasInclude = options.getIncludePackage() != null
    && options.getIncludePackage().hasNext();
if (hasInclude) {
    addPackages(builder, options.getIncludePackage(), true);
```

`getIncludePackage()` is called three times. Whether each call returns a fresh iterator or the same mutable iterator depends on the gomobile binding implementation. If same: `hasNext()` peeks but doesn't consume, and the third call's iterator starts at position 0 (after hasNext peeked). If the getter caches and returns the same iterator, the first element may be incorrectly skipped by `addPackages` (since `hasNext` advanced the internal cursor on some iterator implementations). The code is fragile and depends on an undocumented gomobile contract.

**Fix:** Capture the iterator in a local variable:
```java
StringIterator includeIter = options.getIncludePackage();
boolean hasInclude = includeIter != null && includeIter.hasNext();
if (hasInclude) {
    addPackages(builder, includeIter, true);
}
```

---

### FINDING-P06_android_crossplatform-12
**Severity: P2 (executor churn on screen toggle)**
**Source Anchor:** `VpnRouterService.java:1181-1196`

**Mechanism:** `startStatsPoller()` calls `stopStatsPoller()` then creates a new `ScheduledExecutorService` each time. `initScreenStateReceiver()` calls `startStatsPoller`/`stopStatsPoller` on every `SCREEN_ON`/`SCREEN_OFF` broadcast. Rapid screen toggling (e.g., ambient display, double-tap-to-wake accidental activations) creates and destroys executor pools rapidly. Each `shutdownNow()` returns immediately but the daemon thread may linger briefly.

**Fix:** Reuse a single `ScheduledExecutorService` and toggle scheduling via `ScheduledFuture.cancel()`:
```java
private ScheduledFuture<?> statsFuture;
private synchronized void startStatsPoller() {
    stopStatsPoller();
    if (!isScreenOn || statsPoller == null) return;
    statsFuture = statsPoller.scheduleWithFixedDelay(...);
}
private synchronized void stopStatsPoller() {
    if (statsFuture != null) { statsFuture.cancel(false); statsFuture = null; }
}
```

---

### FINDING-P06_android_crossplatform-13
**Severity: P2 (unbounded HTTP response read)**
**Source Anchor:** `VpnRouterService.java:1231-1234`

**Mechanism:** `pollStatsOnce()` reads the entire Clash API response into a `ByteArrayOutputStream` with no size limit:
```java
byte[] tmp = new byte[4096];
int n;
while ((n = is.read(tmp)) > 0) buf.write(tmp, 0, n);
```

If the Clash API's `/connections` endpoint returns a large response (hundreds of active connections, each with metadata), this allocates unbounded memory on the stats poller thread every 2 seconds. On a memory-constrained Android device, this contributes to GC pressure and potential OOM.

**Fix:** Cap the read to 64 KB:
```java
int totalRead = 0;
while ((n = is.read(tmp)) > 0 && totalRead < 65536) {
    buf.write(tmp, 0, n);
    totalRead += n;
}
```

---

### FINDING-P06_android_crossplatform-14
**Severity: P2 (Windows Service fire-and-forget orphan processes)**
**Source Anchor:** `VPNRouterService.cs:107-113,494-505`

**Mechanism:** `AutostartZapretAsync` and `AutostartTgProxyAsync` are launched as unawaited `_ = ...` tasks. `StopAsync` awaits `_startupComplete` (which signals after VPN start only), then calls `_zapret?.Stop()` / `_tgProxy?.Stop()`. If Zapret is still in its `ResilientStarter` backoff loop when `StopAsync` runs, `_zapret` is null → `?.Stop()` is a no-op → the background task eventually creates and starts `ZapretManager` after the service has stopped → orphaned `winws.exe`.

**Fix:** Track the background tasks and await them in `StopAsync`:
```csharp
private Task? _zapretTask, _tgProxyTask;
// In ExecuteAsync:
_zapretTask = AutostartZapretAsync(settings, stoppingToken);
// In StopAsync, after _startupComplete:
if (_zapretTask is not null) await Task.WhenAny(_zapretTask, Task.Delay(5000));
```

---

## 7. Summary

| Severity | Count | Key Themes |
|----------|-------|------------|
| **P0** | 3 | Traffic fail-open (self-inclusion routing loop), async void crash, dual Go-runtime init |
| **P1** | 6 | State desync, health false positive, handler blocking, header injection, FGS restriction, exported receiver |
| **P2** | 5 | Interrupt suppression, iterator fragility, executor churn, unbounded read, orphan processes |
| **Total** | **14** | |

### Cross-Platform Architectural Notes

1. **No shared state machine abstraction**: Windows and Android implement completely independent lifecycle models with no common interface or behavioral contract. Feature parity is maintained by convention, not by code.
2. **Config hot-reload asymmetry**: Windows can hot-reload via Clash API without tunnel restart; Android requires full teardown/rebuild. This creates user-visible behavioral differences.
3. **Ownership/deference model mismatch**: Windows uses `TunOwnershipLock` (global named semaphore) for multi-process coordination; Android relies on OS-level VPN exclusivity. The service deference logic exists only on Windows.
4. **Three-engine vs single-engine**: Windows service manages VPN + Zapret + TgProxy independently (with the fire-and-forget race documented in FINDING-14); Android manages only VPN (Zapret is in-tunnel, TgProxy absent).
