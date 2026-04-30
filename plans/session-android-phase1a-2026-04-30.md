# Android Phase 1.A — session log 2026-04-30

**Status**: Phase 1.A landed and verified on real hardware. Phase 1.B
blocked on libbox.aar build (Windows + NDK toolchain quirks; retry in
progress with NDK 28).

## What Phase 1.A delivered

| Item | State |
|---|---|
| `VPNRouter.Android/VpnRouterService.cs` | C# port of Kotlin skeleton — extends `Android.Net.VpnService`, implements ACTION_START / ACTION_STOP Intent contract, foreground notification, Builder.AddAllowedApplication for per-app routing, AddDisallowedApplication for self-exclusion. libbox handoff still TODO. |
| `VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs` | Real Intent dispatch via Mono.Android — Start/Stop ship Intent through Application.Context, IsRunningAsync probes Clash API. Replaces Phase 0 stub log calls. |
| `VPNRouter.Android/MainActivity.cs` | AppCompat theme fix from on-device Phase 0 crash (Material → AppCompat.Light.NoActionBar). |
| `VPNRouter.Android/AndroidApp.axaml.cs` | Greeting text bumped to "Phase 1.A — VpnRouterService registered". |

## On-device verification (KYOCERA A101BM, Android 12, arm64-v8a)

```
$ adb shell dumpsys package com.ninitux.vpnrouter | grep VpnRouterService
android.net.VpnService:
  com.ninitux.vpnrouter/.VpnRouterService
  filter Action: "android.net.VpnService"
  permission android.permission.BIND_VPN_SERVICE
```

Service correctly registered with `BIND_VPN_SERVICE` permission, intent
filter matches `android.net.VpnService` action. App boots cleanly:

```
04-30 14:16 I AVALONIA: Surface Created
04-30 14:16 I AVALONIA: Surface Changed
04-30 14:16 I ActivityTaskManager: Displayed +1s526ms
```

Screenshot stored at `C:/Users/vboxuser/Desktop/vpnrouter-android-phase1a-screenshot.png`.

## Build environment landed on this VM

| Tool | Version / path |
|---|---|
| .NET SDK | 8.0.419 (`dotnet`) |
| JDK | Temurin 17.0.17 (`C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot`) |
| Go | 1.26.2 |
| Android SDK | `C:\Users\vboxuser\AppData\Local\Android\Sdk` (cmdline-tools, build-tools, platforms 34) |
| Android NDK | 27.3.13750724 + 28.0.13004108 (sing-box preferred) |
| CMake | 3.31.6 |
| gomobile / gobind | SagerNet fork at `~/go/bin/` (standard fork hardcodes API 16 reject for NDK 27+) |

## Phase 1.B blocker — libbox.aar build

Attempting `go run ./cmd/internal/build_libbox -target android` against
upstream sing-box `v1.13.10` from `tools/sing-box-upstream/`.

### Attempt 1 — NDK 27.3.13750724

Failed at link stage:

```
ld.lld: error: cronet-go/lib/android_arm64@v0.0.0-20260413092954-cd09eb3e271b
        /libcronet.a(stdlib_stdexcept.o):(.rodata+0x4):
        unknown relocation (315) against symbol typeinfo for std::logic_error
```

The cronet-go module ships a precompiled `libcronet.a`. The relocation
type 315 isn't recognised by NDK 27's `ld.lld`. Looks like the lib was
built against a newer NDK.

### Attempt 2 — NDK 28.0.13004108 (in progress)

`build_shared.findNDK()` in upstream sing-box hardcodes
`fixedVersion = "28.0.13004108"`, so the build script picks NDK 28 when
present. Currently compiling — multiple `go.exe` worker processes
active. Hard to predict outcome on Windows host (gomobile + CGO + NDK
is fragile across build hosts).

### Fallback path if both NDK versions fail

1. **Build on Mac via SSH** — `slovn@192.168.0.246` already has NDK access via Android Studio. Run libbox build there, scp the `.aar` back to the VM.
2. **GitHub Actions workflow** — Linux runner, free, deterministic. Add `.github/workflows/build-libbox.yml` that runs `go run ./cmd/internal/build_libbox -target android` on push to a `libbox-build` tag and uploads the resulting `.aar` as a release asset. Then `VPNRouter.Android` downloads it at build time.
3. **Pin a known-good cronet-go version** — replace the `replace` in upstream sing-box's `go.mod` with an older cronet-go commit that has a libcronet.a built against an older NDK.

GitHub Actions is the most robust — frees developers from "did NDK X work for you" issues. Recommend setting that up if NDK 28 attempt also fails.

## What still blocks a working tunnel (Phase 1.B → 1.E)

Even with libbox.aar in hand:

1. **`VpnRouterService.StartTunnel` libbox handoff** — call `Libbox.NewService(_pendingConfigJson, platformInterface)` with the TUN file descriptor from `VpnService.Builder.Establish()`.
2. **`VpnRouterPlatformInterface : Libbox.PlatformInterface`** — Kotlin/Java reference impl is in `sagernet/sing-box-for-android` at `app/src/main/java/io/nekohasekai/sfa/bg/proxy/PlatformInterfaceImpl.kt`. ~200 lines covering DNS resolve callbacks, getuid lookups, route table queries.
3. **`MainActivity.OnConsentResult`** — wire `VpnService.Prepare()` → `StartActivityForResult` → `OnActivityResult` so the system consent dialog can fire before `AndroidSingBoxRuntime.Start`.
4. **`VpnEngine.cs` Android branch** — `#if PLATFORM_ANDROID` path that calls `_androidRuntime.Start(configJson, profile.AndroidPackages)` instead of `_singBoxManager.StartAsync(config)`.
5. **`ConfigGenerator.cs` Android branch** — skip `process_name → proxy` route rules when generating config for Android (per-app routing happens at `VpnService.Builder.AddAllowedApplication` layer).
6. **Smoke test on device** — install APK, tap Connect, accept consent dialog, verify `https://ifconfig.me` returns proxy IP not carrier IP.

## What we now know that we didn't know yesterday

1. **Avalonia.Android requires Theme.AppCompat** — Material throws `IllegalStateException` at `setContentView`. Fix shipped commit `91a8f3f`.
2. **Mono.Android handles Java callable wrappers from `[Service]`** — no Kotlin compiler needed in toolchain. `VpnRouterService.cs` extends `Android.Net.VpnService` directly and registers via attribute.
3. **Standard golang.org/x/mobile is incompatible with NDK 27+** — `gomobile init` rejects with "unsupported API version 16 (not in 21..35)". Need SagerNet fork from `github.com/sagernet/gomobile`.
4. **Building libbox on Windows is fragile** — NDK toolchain works for desktop apps but Go + CGO + Android NDK + cronet-go has cross-host quirks. Linux/Mac CI is more reliable.
5. **`PackageManager.NameNotFoundException` is a checked-equivalent** — must wrap `AddDisallowedApplication(packageName)` even though `packageName` is our own.

## Next session checklist

1. Resolve libbox.aar build (either NDK 28 retry succeeds, or set up GitHub Actions workflow on Linux).
2. Drop `libbox.aar` into `VPNRouter.Android/Lib/` and uncomment AndroidLibrary in csproj.
3. Implement `VpnRouterPlatformInterface` (port Kotlin reference).
4. Wire `Libbox.NewService(...)` call inside `VpnRouterService.StartTunnel`.
5. Wire VpnService.Prepare consent flow in MainActivity.
6. VpnEngine + ConfigGenerator PLATFORM_ANDROID branches.
7. Smoke test on device — verify external IP via `ifconfig.me` swaps from carrier to proxy when connected.

Reference plan: `plans/vpnrouter-android-phase1-roadmap.md`.

## Cross-references

- `91a8f3f` — fix(android-phase0): use AppCompat theme so AvaloniaActivity boots
- `5ac49d6` — feat(android-phase1a): VpnRouterService.cs + Intent dispatch wiring
- `442026f` — docs(android-phase1a): update Phase 0 stub greeting to Phase 1.A
- v2.30.0 stable shipped today (2026-04-30) — Rules page; not Android-related but
  same-day stable cut for context.
