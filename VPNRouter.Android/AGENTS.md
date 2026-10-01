# VPNRouter.Android Zone Instructions

This zone file governs `VPNRouter.Android` and all descendant paths (`Controls/`, `Json/`, `Lib/`, `Resources/`, etc.).

## Overview & Target Framework

Android port of VPNRouter using Avalonia 12.x UI engine targeting `net10.0-android36.0` with `PLATFORM_ANDROID` defined.
Source-links `VPNRouter.Core` directly (`<Compile Include="..\VPNRouter.Core\**\*.cs" LinkBase="Core" />`) so Core's `#if PLATFORM_ANDROID` branches activate during assembly compilation.

## Quick Verification & Build Commands

Canonical test oracle: `docs/agent-contract.md`.

Run shared Android-logic unit tests (not an APK or AndroidApp surface check):
```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~AndroidStorageSaneTests|FullyQualifiedName~AndroidDpiBypassInjectorTests"
```

Release APK Build (requires local `libbox.aar` in `VPNRouter.Android/Lib/`):
```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "<Temurin 17 JDK path>"
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release `
  /p:EnableAndroidTarget=true `
  /p:AndroidSdkDirectory=$env:ANDROID_HOME `
  /p:JavaSdkDirectory=$env:JAVA_HOME
```
Output artifact: `bin\Release\net10.0-android36.0\com.ninitux.vpnrouter-Signed.apk`.

## Layout & Mapped Directories

- `VPNRouter.Android/`: Project root. `MainActivity.cs` and `MainApplication.cs` own Android lifecycle bootstrap; `AndroidStorage.cs` and `AndroidUpdater.cs` own persisted state and update flow. `AndroidApp.axaml.cs` plus sibling partials (`AdvancedShell`, `AutoUpdate`, `ConfigShare`, `CustomConfig`, `DpiBypass`, `FreeConfigs`, `KebabMenu`, `Notifications`, `PerAppFilter`, `Permissions`, `Profiles`, `QrScanApply`, `ServerList`, `SettingsHandlers`, `SubscribePage`, `TestHook`, `TileMenu`, `Tools`, `UiBindings`, `VpnLifecycle`) own Avalonia UI/runtime orchestration.
- `VpnTileService.cs` and `VpnRouterService.java`: the quick-settings tile reads the single connection-state record (`vpn_state`, `vpn_state_reason`, `vpn_state_pid`, `vpn_state_at_ms` in the `vpnrouter_settings` preferences) that `VpnRouterService` writes on every transition; the rules for staleness, tap actions and tile texts live in `VPNRouter.Core/Services/VpnTileLogic.cs` and are unit-tested there. The tile is registered by attributes on its class, not in `AndroidManifest.xml`.
- `TestHookReceiver.cs` and `AndroidApp.TestHook.cs`: an exported adb test receiver, compiled ONLY into builds made with `-p:VpnRouterTestHook=true` (define `VPNROUTER_TESTHOOK`, versionName gets `+testhook`). Release and CI builds never set it; `AndroidTestHookGuardTests` enforces that. Never distribute such an APK.
  Usage (start the app first; UI actions need it in the foreground process; the answer is one JSON line in the broadcast result and in `adb logcat -s VpnRouterTest`; URLs, uuids and tokens are never printed):
  ```
  H="-n com.ninitux.vpnrouter/com.ninitux.vpnrouter.TestHookReceiver"
  adb shell am broadcast $H -a com.ninitux.vpnrouter.TEST_DUMP_STATE
  adb shell am broadcast $H -a com.ninitux.vpnrouter.TEST_SET_CONFIG --es value "<share link | http(s) subscription URL | custom JSON>"
  adb shell am broadcast $H -a com.ninitux.vpnrouter.TEST_CONNECT      # same handler as the Connect button; consent stays the system dialog
  adb shell am broadcast $H -a com.ninitux.vpnrouter.TEST_DISCONNECT
  adb shell am broadcast $H -a com.ninitux.vpnrouter.TEST_RESET        # clears saved config, servers, subscriptions and the state record
  adb shell am broadcast $H -a com.ninitux.vpnrouter.TEST_SET_HERO --es value off|connecting|connected|error   # status ring look only, no state change
  ```
- `VPNRouter.Android/Controls/`: Custom Avalonia controls for Android UI: `StatusCard.cs` (state hero) and `IconView.cs` (`IconView` draws a Lucide icon from `VPNRouter.Core/Services/UiIcons.cs`; `IconLabel` is icon plus text for buttons). Use them instead of text symbols or emoji; see `tools/AGENTS.md` for adding icons.
- `VPNRouter.Android/Json/`: STJ JSON contexts (`AndroidJsonContext.cs`).
- `VPNRouter.Android/Lib/`: Local AAR and JAR libraries (`libbox.aar`, `zxing-android-embedded-4.3.0.aar`, `zxing-core-3.5.3.jar`).
- `VPNRouter.Android/Resources/`: Android XML configs (`xml/file_paths.xml`) and launcher drawables (`mipmap-*/`).

## AndroidApp partial-class architecture

- `AndroidApp : Avalonia.Application` is the cross-platform application entry point split across partial files. `AndroidApp.axaml.cs` manages constructor, framework initialization, shared fields, and cross-concern orchestration; partial sibling files own specific feature surfaces.
- The former AndroidApp source-hash characterization test is absent from the current suite. Before a mechanical split, establish an applicable characterization baseline; shared Core unit tests do not prove preservation of the Android UI/member surface. The September 16 test-prune entry in `plans/OPEN-DEFECTS.md` tracks the missing coverage.

## Java Sources & SingBox Native Runtime

- `VpnRouterService.java`: Native Android service managing tunnel lifecycle.
  - Implements `START_STICKY` for kernel service recreation under memory pressure.
  - Calls `startForeground` using `FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED` on API 34+.
  - Holds a fail-safe connect `WakeLock` for the active tunnel lifetime.
  - Implements `onTaskRemoved` swipe-away recovery: schedules self-restart via `AlarmManager` when battery optimization exemption is granted and tunnel was running (no-ops if `boxService` is already live).
  - Wraps `startForeground` in try/catch to broadcast `foreground-start-blocked` safely on background start restrictions.
- `AndroidDeepVerifyBox.java`: Embedded Java helper spinning transient sing-box service instances for Free Configs deep verification.
- `QrScanLauncher.java`: Java bridge to ZXing embedded scanner for live QR scan detection.
- `SlipstreamNative.java`: JNI binding for DNS-tunnel sidecar (`libslipstream_jni.so`).
- `libbox.aar`: SingBox gomobile binding imported in `VPNRouter.Android.csproj` with `Bind="false"` to bypass C# binding generator overhead and prevent GC-bridge initialization issues. Interacted with via JNI / Java service layer.
