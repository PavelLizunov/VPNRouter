# VPNRouter.Android Zone Instructions

This zone file governs `VPNRouter.Android` and all descendant paths (`Controls/`, `Json/`, `Lib/`, `Resources/`, etc.).

## Overview & Target Framework

Android port of VPNRouter using Avalonia 12.x UI engine targeting `net10.0-android36.0` with `PLATFORM_ANDROID` defined.
Source-links `VPNRouter.Core` directly (`<Compile Include="..\VPNRouter.Core\**\*.cs" LinkBase="Core" />`) so Core's `#if PLATFORM_ANDROID` branches activate during assembly compilation.

## Quick Verification & Build Commands

Canonical test oracle: `docs/agent-contract.md`.

Run unit and shared Android-logic characterization tests:
```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~AndroidAppCharacterizationTests|FullyQualifiedName~AndroidStorageSaneTests|FullyQualifiedName~AndroidDpiBypassInjectorTests"
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

- `VPNRouter.Android/`: Project root. `MainActivity.cs` and `MainApplication.cs` own Android lifecycle bootstrap; `AndroidStorage.cs` and `AndroidUpdater.cs` own persisted state and update flow. `AndroidApp.axaml.cs` plus sibling partials (`AdvancedShell`, `AutoUpdate`, `ConfigShare`, `CustomConfig`, `DpiBypass`, `FreeConfigs`, `KebabMenu`, `Notifications`, `PerAppFilter`, `Permissions`, `Profiles`, `QrScanApply`, `ServerList`, `SettingsHandlers`, `SubscribePage`, `Tools`, `UiBindings`, `VpnLifecycle`) own Avalonia UI/runtime orchestration.
- `VPNRouter.Android/Controls/`: Custom Avalonia controls for Android UI (`StatusCard.cs`).
- `VPNRouter.Android/Json/`: STJ JSON contexts (`AndroidJsonContext.cs`).
- `VPNRouter.Android/Lib/`: Local AAR and JAR libraries (`libbox.aar`, `zxing-android-embedded-4.3.0.aar`, `zxing-core-3.5.3.jar`).
- `VPNRouter.Android/Resources/`: Android XML configs (`xml/file_paths.xml`) and launcher drawables (`mipmap-*/`).

## AndroidApp Partial-Class Architecture & Member Surface Invariant

- `AndroidApp : Avalonia.Application` is the cross-platform application entry point split across partial files. `AndroidApp.axaml.cs` manages constructor, framework initialization, shared fields, and cross-concern orchestration; partial sibling files own specific feature surfaces.
- **Wave 9 Characterization Invariant**: `VPNRouter.Tests/AndroidAppCharacterizationTests` pins a source-derived SHA-256 hash of all member declarations across `AndroidApp*.cs` files. Any extraction or refactoring that drops, renames, or alters member signatures will fail this test. Update `PinnedHash` in `AndroidAppCharacterizationTests` only when intentional surface modifications are made.

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
