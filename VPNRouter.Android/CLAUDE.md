# VPNRouter.Android

Android port. Same Avalonia 12.0.3 UI engine as desktop App, different
SingBox runtime path (libbox.aar via gomobile-bound JNI, not the
desktop's spawned `sing-box.exe`).

## Target framework

`net10.0-android36.0` (`<PLATFORM_ANDROID>` define). Source-links
`VPNRouter.Core` directly (no ProjectReference — Core ships as
`net8.0` and the RID-restored multi-target deadlock made
ProjectReference fragile pre-Phase 1.E). Core's `#if PLATFORM_ANDROID`
branches activate when compiled into this assembly.

## AndroidApp partial-class layout

The Android port hosts a god-class `AndroidApp : Avalonia.Application`
that is the cross-platform entry point. After Phase 2C (Wave 9,
2026-05-18) it spans 14 partial files. The main file
(`AndroidApp.axaml.cs`) holds the constructor / framework init / shared
field declarations / cross-concern orchestration; each sibling partial
owns one concern.

```
AndroidApp.axaml             ← XAML scaffolding (App.Resources only)
AndroidApp.axaml.cs          ← OnFrameworkInitializationCompleted, ApplyTheme,
                                BuildSimplePageView, kebab menu wiring,
                                language toggle, app picker, custom-categories
                                shell (~4,900 LOC after 2C split)

# Phase 2C Wave 9 extractions (2026-05-18)
AndroidApp.Notifications.cs  ← log viewer overlay, crash log viewer,
                                toast feedback, recovery-notice surfacing
                                (ConsumeAndSurfaceRecoveryNotice, BuildLogOverlay,
                                LoadLogContent, LoadCrashLogContent,
                                ShowMenuFeedback, CopyToClipboard)
AndroidApp.Permissions.cs    ← system-permission UX (Always-on VPN deep-link,
                                battery-optimization request,
                                auto-reconnect-on-network-change toggle)
AndroidApp.VpnLifecycle.cs   ← tunnel intent → UI state machine
                                (Connect/Disconnect dispatch, chip state
                                transitions, status card, diagnostics pump,
                                health probe, error one-liner)
AndroidApp.UiBindings.cs     ← Settings/Network tab builders + card helpers
                                (BuildNetworkTabContent → 6 sub-sections +
                                MakeRadioCard / MakeCheckboxCard /
                                MakeLabeledCheckboxRow / MakeAutostartRow /
                                MakeSectionTitle / WrapSection)

# Pre-2C partials (still present)
AndroidApp.AdvancedShell.cs  ← Advanced overlay shell + per-tab content host
AndroidApp.AutoUpdate.cs     ← auto-update banner + APK download/install
AndroidApp.ConfigShare.cs    ← export/import overlays
AndroidApp.DpiBypass.cs      ← DPI bypass tab content
AndroidApp.FreeConfigs.cs    ← Free Configs page (master-detail)
AndroidApp.QrScanApply.cs    ← Bug-AND-023 QR scan magic 1-action apply
AndroidApp.ServerList.cs     ← per-subscription server testing UI
AndroidApp.SubscribePage.cs  ← Subscribe tab UI
AndroidApp.Tools.cs          ← Tools tab (Zapret + Telegram, Phase D merge)
```

## Wave 9 invariant

The partial-class extraction must NOT change the AndroidApp public/
private member set. The `VPNRouter.Tests/AndroidAppCharacterizationTests`
pins a source-derived SHA-256 of every declaration across all
`AndroidApp*.cs` files. Any extraction that drops, renames, or
signature-changes a member (intentional or not) trips that test. To
re-pin the hash after an intentional surface change, run the test once
to capture the new value and update the `PinnedHash` constant.

Source-derived (not reflection-derived) because `VPNRouter.Tests` is
`net8.0` and cannot ProjectReference / Assembly.Load a `net8.0-android`
type — see `AndroidAppSourceSurfaceHashHelper.cs` class doc for the
parser details.

## Java sources

`VpnRouterService.java` + `AndroidDeepVerifyBox.java` +
`QrScanLauncher.java` are compiled by .NET Android via the
`<AndroidJavaSource>` ItemGroup. They depend on `libbox.aar`
(sing-box gomobile binding) which is a private build excluded from
the repo. CI cannot build the full APK until libbox.aar is available;
local-only builds require placing the aar at `VPNRouter.Android/Lib/`.

## Build (when libbox.aar is present)

```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "<Temurin 17 JDK path>"
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release `
  /p:EnableAndroidTarget=true `
  /p:AndroidSdkDirectory=$env:ANDROID_HOME `
  /p:JavaSdkDirectory=$env:JAVA_HOME
```

Output: `bin\Release\net8.0-android\com.ninitux.vpnrouter-Signed.apk`.

## Phase 0 historical scaffolding

See `VPNRouter.Android/README.md` for Phase 0 → Phase 1 setup notes
(SDK install, libbox.aar build steps). That doc is outdated but kept
for the lineage record.
