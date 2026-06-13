# Android status-card "stuck Connected · 0:00" — lifecycle investigation

Date: 2026-06-13
Context: observed on A101BM during v2.42.0-r17 device testing. Spawned as
`task_b0cad072` follow-up (see MEMORY.md r17 entry). Pre-existing, NOT an
r17 regression.

## The question

The Simple-page status card was seen stuck on **"Connected · 0:00"** (green
dot, Disconnect CTA, no system VPN-key icon) *after* the tunnel was actually
torn down. Logical state was correct (tapping the stale "Disconnect" performed
a **Connect**; a fresh force-stop + relaunch showed "Not connected").

Leading hypothesis at spawn time: `OnIntentChanged` is bound to a **detached**
`AndroidApp` instance (the `s_currentLifecycleSubscriber` `Interlocked.Exchange`
hand-off), so `UpdateConnectionState(false)` mutates a non-visible instance's
`_statusCard` while the visible Avalonia surface renders the old instance's card.

**Task:** does this reproduce under NORMAL Android lifecycle (recents, rotation,
dark-mode, process-death-restore) — or only under the `monkey -c LAUNCHER`
relaunch storm used in testing? If only monkey → test artifact (document +
close). If normal → real bug, fix the subscriber↔visible binding.

## Determination

**The hypothesized multi-instance desync CANNOT occur under normal Android
lifecycle on Avalonia 12. It is a test artifact.** A genuine — but *distinct
and milder* — normal-lifecycle desync does exist (lost broadcast + no resume
re-sync); see "Residual real bug" below.

## Why the multi-instance hypothesis is impossible (Avalonia 12 evidence)

The whole `s_currentLifecycleSubscriber` machinery assumes multiple live
`AndroidApp` instances can exist in one process. That was true in **Avalonia
11.x**, where `AvaloniaMainActivity<TApp>` built the Avalonia `Application` from
the *Activity's* `OnCreate` via `CreateAppBuilder()` — so every Activity
recreation minted a new App. The Wave 23 / Phase 5 migration (2026-05-18,
commit c33e372) moved to **Avalonia 12** and `AvaloniaAndroidApplication<TApp>`.
Decompiling `Avalonia.Android.dll` 12.0.3 (the version pinned in
`VPNRouter.Android.csproj`) shows the App is now a **process singleton**:

1. `AvaloniaAndroidApplication<TApp>.OnCreate()` (the **Android** `Application`
   OnCreate — runs **once per process**) calls `InitializeAppLifetime()` →
   `builder.SetupWithLifetime(...)`. This is the *only* place the Avalonia
   `Application` (our `AndroidApp`) is constructed and the *only* place
   `OnFrameworkInitializationCompleted` runs. There is no other
   `AppBuilder.Configure<AndroidApp>()` / `new AndroidApp()` anywhere in the
   repo (grep-verified).

2. `OnFrameworkInitializationCompleted` builds the view **once**
   (`BuildSimplePageView()` at `AndroidApp.axaml.cs:536`) and assigns
   `singleView.MainView = view`. The Avalonia.Android `ApplicationLifetime`
   setter then captures it as a singleton factory:
   ```csharp
   // Avalonia.Android.ApplicationLifetime.MainView setter
   if (_mainView != null) MainViewFactory = () => _mainView;   // same instance
   ```

3. On **every Activity (re)creation**, `AvaloniaActivity.OnCreate` →
   `AvaloniaMainActivity.InitializeAvaloniaView(_content)` (with `_content`
   null on a fresh Activity) →
   ```csharp
   if (initialContent == null) initialContent = lifetime.MainViewFactory?.Invoke();
   _view = new AvaloniaView(this);   // NEW Android view container
   base.Content = initialContent;     // ...re-parents the SAME _mainView
   ```
   i.e. a new `AvaloniaView` wraps the **same** `_mainView` Control tree. The
   `AndroidApp` instance, its `_statusCard` field, and its single
   `MainActivity.IntentChanged` subscription are untouched.

**Consequence:** there is exactly **one** `AndroidApp`, **one** `_statusCard`,
and **one** lifecycle subscription per process. `s_currentLifecycleSubscriber`
can only ever hold that one instance; `AttachLifecycleEvents`' `if
(_lifecycleEventsAttached) return;` guard means it never double-subscribes. The
visible card is always the App's current `_statusCard` field, and every state
path (`OnIntentChanged` → `UpdateConnectionState`, the diagnostics timer's
`OnDiagnosticsTick`) reads that same field. A "detached instance gets the
event while a different visible instance renders" split is structurally
unreachable in a single process.

The only in-process MainView *rebuild* — `RebuildSimplePageView()` on a
light/dark theme switch (`AndroidApp.axaml.cs:3354`) — stays on the same
`AndroidApp`, reassigns `_statusCard` to the freshly-built control, and
**explicitly re-syncs** with `UpdateConnectionState(MainActivity.IntendedConnected)`.
So even a theme switch keeps everything consistent.

### Why this matches the evidence

To get two live `AndroidApp` instances you need two `Application.OnCreate`
calls = **two processes**. But the exact reported symptom — "Connected · **0:00**"
with the timer frozen — cannot come from a second *process*: a fresh process
resets the `static _intendedConnected` to `false`, so
`OnFrameworkInitializationCompleted` runs `UpdateConnectionState(false)` →
"Not connected". That is precisely the "fresh force-stop + relaunch showed Not
connected" observation. The "0:00 frozen + Disconnect CTA + IntendedConnected
false" combination is only producible by **two in-process AndroidApps** (older
one frozen at 0:00 by `DetachLifecycleEvents` → `DisposeDiagnosticsTimer`, while
it remains the visible surface) — which Avalonia 12 does not create. It was an
artifact of the messy interactive session: repeated `monkey -c LAUNCHER`
relaunches + adb-driven force-stops + the harness caveat that "Avalonia renders
to ONE SurfaceView so uiautomator only sees SYSTEM dialogs" (i.e. the card
state had to be read by eye across rapid relaunches).

**Action: document + close the multi-instance theory.** `s_currentLifecycleSubscriber`
is now vestigial (Avalonia-11-era); harmless, but it can be removed in a future
cleanup with a note that Avalonia 12 guarantees a single App instance.

## Residual real bug (distinct mechanism, worth a defensive fix)

The card is synced to tunnel reality by exactly two sources:
1. the **optimistic** `SetIntent(true/false)` on Connect/Disconnect button taps
   (`MainActivity.DispatchTunnelStart` / `RequestDisconnect`), and
2. the **`TunnelStateReceiver`** broadcasts (TUNNEL_UP/DOWN/ERROR).

The receiver is registered in `MainActivity.OnCreate` and unregistered in
`OnDestroy` — i.e. it lives on the **Activity** lifecycle. The broadcast is
process-local (`setPackage`) and **not sticky**: if a TUNNEL_DOWN/UP fires while
**no Activity (hence no receiver) is alive**, it is silently dropped. And there
is **no resume re-sync** — `MainActivity` overrides `OnCreate`/`OnDestroy`/
`OnActivityResult` but NOT `OnResume`/`OnStart`, and `OnFrameworkInitializationCompleted`
(the only other re-sync point) runs once per process.

So under normal lifecycle:
- **Activity survives in background** (plain Home/Recents): receiver stays
  registered → broadcasts received → card stays correct. ✅ (the common case)
- **Activity destroyed while backgrounded** — "Don't keep activities" dev
  option, background memory reclaim of the Activity, or an aggressive OEM power
  manager — and the tunnel changes state during that window → broadcast lost →
  `_intendedConnected` is now stale → on return the card shows the **stale**
  state until the next button tap or a process restart. ❗ **Real, normal-lifecycle.**

Note this mechanism's signature differs from the reported one: it produces a
card stuck Connected with an **increasing** uptime (the diagnostics timer keeps
ticking on the live App), not a frozen "0:00". So it is NOT what the tester saw,
but it IS a genuine user-facing desync.

### Fix — IMPLEMENTED 2026-06-13 (the task's suggested remedy, done correctly)

A **resume re-sync** that refreshes the card from the **authoritative** tunnel
state. Re-syncing from `MainActivity.IntendedConnected` alone is insufficient:
when the triggering broadcast was lost, `IntendedConnected` is itself stale. The
service is the only source of truth. **Demote-only** so a fresh process carrying
a stale `tunnel_live=true` (process killed without a clean teardown) can never
falsely promote a fresh Off card to "Connected".

1. **Java (`VpnRouterService`)** — `setTunnelLive(boolean)` writes the
   `tunnel_live` key into the shared `vpnrouter_settings` prefs in lockstep with
   the broadcasts: `true` right after `ACTION_TUNNEL_UP`; `false` right after
   `ACTION_TUNNEL_DOWN`, on the `startTunnel` error path, and on
   `foreground-start-blocked`. Same prefs file `AndroidStorage` reads; survives
   the no-receiver window.
2. **C# (`AndroidStorage.GetTunnelLive()`)** — reads the flag (default false).
3. **C# (`MainActivity.OnResume`)** — calls
   `TunnelStateResync.TryResolveOnResume(IntendedConnected, GetTunnelLive(), out var corrected)`;
   on a stale falsely-"Connected" card it `SetIntent(false)`, driving the
   existing `IntentChanged → UpdateConnectionState` path. Demote-only, so the
   connecting window (optimistic `IntendedConnected=true`, `tunnel_live` still
   false) can briefly demote on a resume that lands mid-connect — vanishingly
   rare (needs Activity-destroy within the ~1–3 s connect window) and
   self-corrects on the next `TUNNEL_UP`. False-Off is cosmetic; false-On
   (the dangerous one) is eliminated.
4. **Core (`VPNRouter.Core/Services/TunnelStateResync.cs`)** — pure decision
   helper (net8.0, no Android deps) so it sits off the `AndroidApp` hash-pinned
   surface and is unit-testable from the net8.0 test project.
5. **Test (`VPNRouter.Tests/TunnelStateResyncTests.cs`)** — 4 cases pinning
   demote-only behaviour (esp. "Off + stale live=true → does NOT promote").

Also done: removed the vestigial multi-instance machinery
(`s_currentLifecycleSubscriber` + `DetachLifecycleEvents` + `DisposeDiagnosticsTimer`)
from `AndroidApp.VpnLifecycle.cs`; re-pinned the source-surface hash
(`AndroidAppCharacterizationTests` → `b55220aa…`).

**Verification status:** `dotnet test` 37/37 green (incl. the 4 new + re-pinned
characterization); Android `dotnet build -c Release` 0 errors (signed APK built).

### UPDATE 2026-06-13 — device testing found r18 INSUFFICIENT → r19 dual-signal fix

On-device DKA verification on the A101BM (via Mac adb) exposed a gap the
flag-only r18 fix could not cover. Repro: connect (VLESS) → "Don't keep
activities" ON → background (Activity destroyed) → disconnect the tunnel from
the **system VPN Settings** → reopen. Result on r18: tunnel definitively down
(0 VPN networks, no tun, no key icon) but the card stayed **"Connected · 2:13"**.
Diagnostic build logged `OnResume: IntendedConnected=True, tunnel_live=True`.

Root cause: on this OEM (KYOCERA / Android 12) the system-Settings VPN disconnect
tears down the tun **without invoking `VpnService.onRevoke`** — so the service
never runs `stopTunnel`, never broadcasts `TUNNEL_DOWN`, and never writes
`tunnel_live=false`. The flag stays stale-`true`, so r18's `!tunnel_live`
condition was false and it (correctly, per its own logic) did not demote. This
is a *silent tun death* the service simply does not notice (it has no in-service
health monitor).

**r19 fix:** the resume re-sync now also consults the platform **ground truth** —
`ConnectivityManager` enumerated for an active `TRANSPORT_VPN` network
(`MainActivity.IsVpnTransportActive`). Decision (in the pure
`TunnelStateResync.TryResolveOnResume(intended, tunnel_live, vpnActive)`):
demote when `intended && (!tunnel_live || !vpnActive)`. Fail-safe: an
undeterminable VPN state returns `true` (treat as active → never a false Off).
This covers explicit-stop-lost-broadcast (flag) AND silent tun death (transport).

**r19 device-verified PASS on A101BM (log + screenshot):**
- Negative (genuinely connected, `vpn-net-count=1`): background+foreground →
  resume re-sync absent → no false demote.
- Positive (silent disconnect, `vpn-net-count=0`): reopen →
  `resume re-sync: card showed Connected=True but tunnel is down
  (tunnel_live=True, vpnActive=False) — correcting card to connected=False`;
  screenshot footer flips "Connected · 2:13" → **"Not connected" / "Start VPN"**.

Shipped as `v2.42.0-r19` (supersedes r18). `dotnet test` green incl. dual-signal
`TunnelStateResyncTests`. The original DKA recommendation stands and is now met.

## Secondary finding — `vpn-lifecycle` thread count (7 live, 6 after disconnect)

`lifecycleExecutor` is a **per-instance** field of `VpnRouterService`:
`Executors.newSingleThreadExecutor(...)` with a daemon `"vpn-lifecycle"`
worker. By construction:
- One `VpnRouterService` instance ⇒ exactly **one** core worker thread (single-
  thread executor; no core-thread timeout, so it idles alive between tasks).
- `onDestroy` calls `lifecycleExecutor.shutdown()`, which lets the queued final
  `stopTunnel` drain and then **terminates** the worker.

So a single connect→disconnect cycle nets zero leaked threads. 7 live workers
⇒ ~7 `VpnRouterService` *instances* whose workers had not terminated. A service
instance is created fresh on the first `onStartCommand` after a full
`stopSelf()` — i.e. **every Connect that follows a complete Stop mints a new
instance + new worker**. Going 7→6 on a disconnect is consistent with that
disconnect's `shutdown()` terminating one instance's worker.

Two plausible causes, NOT mutually exclusive:
- **Transient accumulation under rapid cycling** (most likely given the test
  session): `shutdown()` only terminates the worker once its current task
  returns. The B1 teardown bounds (`runBounded("nativeStop"/"boxService.close",
  4s)`) cap a stuck dns-tunnel QUIC-backoff teardown, but the worker can still
  be busy up to ~8s; a fast Connect/Stop/Connect storm stacks several
  not-yet-drained daemons that *do* eventually exit. This is benign.
- **Genuine leak** only if an old instance's `onDestroy` never runs (worker
  stays a live daemon holding the instance via its captured `Runnable`s). Not
  proven from code.

**Verdict:** by construction this is self-cleaning, not a per-connect permanent
leak. The observed 7 is most consistent with transient pile-up during the
relaunch/connect storm. **Confirm on device** with a clean before/after:
`adb shell "cat /proc/<pid>/task/*/comm | grep -c vpn-lifecycle"` at idle, then
after N deliberate connect→(wait TUNNEL_UP)→disconnect→(wait ~10s) cycles. If
the steady-state idle count climbs monotonically and does not settle back to ~1,
it's a real leak; the cheap hardening then is `((ThreadPoolExecutor))…
allowCoreThreadTimeOut(true)` semantics (or replace the single-thread executor
with one whose core thread times out) so an idle drained worker self-reaps.

## Files referenced
- `VPNRouter.Android/AndroidApp.VpnLifecycle.cs` — Attach/Detach, OnIntentChanged,
  UpdateConnectionState, diagnostics timer, `s_currentLifecycleSubscriber`.
- `VPNRouter.Android/AndroidApp.axaml.cs:536` (one-time build),
  `:3354 RebuildSimplePageView` (theme-switch in-process rebuild + re-sync).
- `VPNRouter.Android/MainActivity.cs` — `TunnelStateReceiver` (OnCreate/OnDestroy
  registration), `SetIntent`, no `OnResume`.
- `VPNRouter.Android/VpnRouterService.java` — `lifecycleExecutor`, teardown
  bounds, TUNNEL_UP/DOWN broadcasts.
- Avalonia 12.0.3 `Avalonia.Android.dll` — `AvaloniaAndroidApplication<TApp>`,
  `AvaloniaMainActivity`/`AvaloniaActivity`, `ApplicationLifetime.MainView`.
</content>
</invoke>
