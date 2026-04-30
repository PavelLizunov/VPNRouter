# Android Phase 1.C — session log 2026-04-30 (tunnel UP)

**Status**: Phase 1.C COMPLETE. First working VPN tunnel on Android
hardware — libbox runs in-process, establishes a TUN file descriptor
via `VpnService.Builder`, and starts the sing-box service.

## What Phase 1.C delivered

| Item | State |
|---|---|
| `VpnRouterService.java` | Full Java port — owns VpnService lifecycle, libbox.setup, CommandServer.start, startOrReloadService. Also embeds two private classes implementing `PlatformInterface` (15 callbacks) + `CommandServerHandler` (5 callbacks). |
| `MainActivity.cs` (C#) | VpnService.Prepare → StartActivityForResult → OnActivityResult consent flow + ACTION_START dispatch with Phase 1.C smoke-test config. |
| `AndroidManifest.xml` | Explicit `<service>` registration for the Java class with `BIND_VPN_SERVICE` + `android.net.VpnService` intent-filter. |
| `VPNRouter.Android.csproj` | `<AndroidJavaSource Include="VpnRouterService.java" />` for javac integration; libbox.aar still `Bind="false"`. |

## On-device verification (KYOCERA A101BM, Android 12, arm64-v8a)

Logcat captured the full Phase 1.C lifecycle:

```
21:06:28.041  VpnRouter: Phase 1.C: requesting VPN consent
21:06:28.066  VpnRouter: Phase 1.C: presenting system VPN consent dialog
              (system VPN ConfirmDialog activity displays)
21:06:28.508  VpnRouter: Phase 1.C: consent granted
21:06:28.558  nativeloader: Load libbox.so ok
21:06:28.597  VpnRouter: libbox setup OK (base=/data/user/0/.../files
              working=.../files/data temp=.../cache)
21:06:28.677  Vpn: setting state=CONNECTING, reason=establish
21:06:28.693  ConnectivityService: registerNetworkAgent
              network{101}  IS_VPN
              InterfaceName: tun0
              LinkAddresses: [172.19.0.1/30, fdfe:dcba:9876::1/126]
              DnsAddresses: [172.19.0.2]
              Routes: [0.0.0.0/0 -> tun0, ::/0 -> tun0]
21:06:28.698  Vpn: Established by com.ninitux.vpnrouter on tun0
21:06:28.713  VpnRouter: libbox service started successfully
21:06:28.776  NotifyService: notification VPNRouter / Tunnel active
```

Confirmed via `adb shell ip addr show tun0`:

```
23: tun0: <POINTOPOINT,UP,LOWER_UP> mtu 1500 qdisc pfifo_fast state UNKNOWN
    inet 172.19.0.1/30 scope global tun0
    inet6 fdfe:dcba:9876::1/126 scope global
```

Status bar showed the standard Android VPN key icon — visible
end-user-facing proof.

Screenshot stored at:
`C:/Users/vboxuser/Desktop/vpnrouter-android-phase1c-tunnel-up.png`

## Architectural breakthrough

Phase 1.C started with the C# attempt to consume libbox's auto-generated
bindings (`Bind="true"`). That triggered a Mono GC-bridge abort during
app startup:

```
monodroid-gc: asked if a class System.Object is a bridge
              before we inited java.lang.Object
F libc: Fatal signal 6 (SIGABRT), code -1 (SI_QUEUE)
```

The crash reproduced **with no consuming code** — just having the
`<AndroidLibrary>` item with `Bind="true"` was enough. Likely an issue
with libbox exporting ~80 gomobile-generated `Seq$Proxy` classes whose
volume / shape stresses the binding pipeline's class-registration timing
(some transitive type drags in `java.lang.Object` before the bridge can
ask its own questions about it).

The pivot: **keep all libbox-touching code in Java**. .NET Android has
first-class `<AndroidJavaSource>` support — javac compiles the .java
files directly into the APK, and the resulting Java classes consume
libbox.aar's `io.nekohasekai.libbox.*` types natively without any C#
binding generation. The C# UI (Avalonia) talks to the Java service via
`Intent`s, which is the same contract the consent dialog already uses.

This bypassed the binding generator entirely. Build succeeds, app
boots, libbox runs.

## Bugs caught during Phase 1.C iteration

1. **`commandServer.startOrReloadService(json, null)` panics in libbox**
   with nil-pointer dereference at `command_server.go:175`. Must pass
   `new OverrideOptions()` (default-constructed) instead. Fixed by
   importing `OverrideOptions` and passing it.

2. **`PlatformInterface.useProcFS()` returns `boolean`, not `void`**.
   Initial draft had `void` (probably copying from Kotlin's nullable
   return convention). Fixed.

3. **`startDefaultInterfaceMonitor` / `closeDefaultInterfaceMonitor` /
   `sendNotification` all `throws Exception`** — needed `throws Exception`
   on Java method signatures.

4. **No `writeLog(String)` method** — I'd assumed PlatformInterface had
   one (mirroring our desktop convention) but javap confirmed it
   doesn't. Removed.

5. **AppCompat theme requirement** (carry-over from Phase 0) — Material
   theme triggers `IllegalStateException` at `setContentView`. Already
   fixed in commit `91a8f3f`.

## What's next (Phase 1.D)

Phase 1.C used a smoke-test config with just `direct` outbound — no
proxy server. The TUN is up and routes are programmed, but external
HTTP through the TUN doesn't reach the open internet because libbox's
gVisor stack hands packets back to `direct`, which itself goes through
the TUN (we've configured all routes via tun0). It's a self-contained
verification that the libbox+TUN handoff works end-to-end.

Phase 1.D needs:

1. **Real proxy outbound** — VLESS+Reality config (matching desktop
   subscription format), so traffic out of the tunnel actually hits a
   real upstream proxy.
2. **User-driven Connect / Disconnect** — replace the 3-second
   auto-trigger with a button in shared App.axaml.
3. **`VpnEngine.cs` PLATFORM_ANDROID branch** — Android calls into
   `AndroidSingBoxRuntime.Start(configJson, allowedPackages)` instead
   of spawning desktop `sing-box.exe`.
4. **`ConfigGenerator.cs` PLATFORM_ANDROID branch** — skip `process_name`
   route rules; per-app routing happens at the `VpnService.Builder`
   layer instead.
5. **Connectivity verification** — `curl https://ifconfig.me` through
   the tunnel must report proxy IP, not carrier IP.

## Commits this session (Phase 1.A → 1.C)

| Hash | What |
|---|---|
| `91a8f3f` | fix(android-phase0): AppCompat theme |
| `5ac49d6` | feat(android-phase1a): VpnRouterService.cs (later replaced by Java) + Intent dispatch wiring |
| `442026f` | docs(android-phase1a): greeting text update |
| `2cf23a4` | docs(plans): Phase 1.A session log |
| `8fa7d18` | feat(android-phase1b-foundation): libbox.aar bundled into APK |
| `41cf591` | chore(android-phase1c): bisect libbox runtime wiring; park drafts |
| `82ef6e2` | feat(android-phase1c): VPN tunnel established on real hardware |

## Cross-references

- `plans/vpnrouter-android-phase1-roadmap.md` — original Phase 1 plan
- `plans/session-android-phase1a-2026-04-30.md` — Phase 1.A session log
- `plans/android-phase1c-drafts/` — earlier C# drafts; superseded by
  `VPNRouter.Android/VpnRouterService.java` (kept for reference)
- `VPNRouter.Android/VpnRouterService.java` — final working impl
- `VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs` — Core
  layer Intent-dispatch helper (Phase 1.D will route VpnEngine through it)

## Toolchain summary (this VM)

| Component | Version |
|---|---|
| .NET SDK | 8.0.419 |
| JDK | Temurin 17.0.17 |
| Go | 1.26.2 |
| Android SDK | platforms 34, build-tools, cmdline-tools |
| Android NDK | 28.0.13004108 (sing-box's pinned fixedVersion) |
| CMake | 3.31.6 |
| gomobile / gobind | SagerNet fork at `~/go/bin/` |
| adb (on Mac) | 37.0.0 (Homebrew cask) |

libbox.aar build cmd (one-time, ~30 min):

```bash
cd tools/sing-box-upstream
export ANDROID_NDK_HOME=$ANDROID_HOME/ndk/28.0.13004108
go run ./cmd/internal/build_libbox -target android
cp libbox.aar ../../VPNRouter.Android/Lib/
```

VPNRouter.Android build cmd:

```bash
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release \
  -p:EnableAndroidTarget=true \
  -p:AndroidSdkDirectory=$ANDROID_HOME \
  -p:JavaSdkDirectory=$JAVA_HOME
```
