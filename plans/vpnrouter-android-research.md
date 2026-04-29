# VPNRouter — Android Port Research

**Date**: 2026-04-29
**Baseline**: v2.28.7-r1 (current rolling), v2.28.6 stable.
**Goal**: scope what an Android port costs, identify the load-bearing risks, pick
an MVP path. Solo dev, OSS, no paid deps.

---

## TL;DR

1. **Use sing-box's official Android library `libbox.aar`** (built from
   `sagernet/sing-box-for-android` with `with_android_tunhandler`). No custom
   Go cross-compile, no JNI written by us.
2. **Use Avalonia 11.3 Android target** — reuses ~80% of `VPNRouter.App` XAML
   and 100% of `VPNRouter.Core` minus 6 Windows-specific files. Caveat: still
   prove the touch/Material gaps on a real device before committing.
3. **The hard architectural mismatch is process selection**, not UI: Android
   filters per-app at `VpnService.Builder` time using **package names**, and
   you can't change the allow-list without tearing down the tunnel. Our
   profile/processes catalog needs a parallel `package_id` field.
4. **MVP path = Option A** (Avalonia-Android + libbox.aar wrapped in a
   minimal Kotlin `VpnService` Activity). Estimate **4-6 solo weeks** for
   beta. Distribute via direct APK from `vpn.ninitux.com` + F-Droid.
5. **Skip Google Play for v1** — VPN-category review is slow, KYC adds
   weeks, and changes to anti-cheat / banking apps' "VpnService detection"
   policies make Play submission a moving target. Re-evaluate after Android
   beta is stable.

---

## What's already cross-platform (audit of VPNRouter.Core)

23 files in VPNRouter.Core have OS branches. Split:

| Bucket | What | Android verdict |
|---|---|---|
| **Pure C# (no OS branch)** | `VlessUriParser`, `SubscriptionFetcher`, `SubscriptionResolver`, `VlessServersResolver`, `ConfigGenerator`, `CustomConfigInjector`, `LeakProtection`, `FreeConfig*` (entire `Services/FreeConfigs/`), `ProfileManager`, `SettingsLoader`, `SettingsMigrator`, `Models/*`, `AppVersion`, `TcpTlsProbe`, `VlessDeepVerifier`, `UpdateChecker` | Reuse 1:1 |
| **Has OS branch but no Android impl yet** | `AppPaths.ResolveDataDir`, `DnsFlusher`, `HostsManager`, `NetworkInterfaceDetector`, `RuntimeStatusDetector`, `SingBoxManager`, `VpnEngine`, `Platform/PlatformServices` | Add Android branch |
| **Windows-only — exclude** | `EtwProcessMonitor`, `FirewallManager`, `WindowsDnsHardening`, `Zapret*`, `TgProxy*`, `OrphanCleanup` | `#if !ANDROID` guard |

**Conclusion**: VPNRouter.Core is in significantly better shape for Android
than for Linux a year ago — most platform-specific code is already behind
interfaces (`IProcessScanner`, `IFirewallManager`, `IProcessMonitor`) and
`#if PLATFORM_WINDOWS` blocks. We need a fourth implementation:
`AndroidProcessScanner`, `AndroidFirewallManager` (no-op),
`AndroidProcessMonitor` (no-op), `AndroidVpnService` (the new big one).

---

## The Android VPN model — what's different

### VpnService API (only legal path on non-rooted devices)

Android exposes `android.net.VpnService` for userland VPNs. App extends it,
builds via `Builder`, OS gives a `ParcelFileDescriptor` to TUN-like fd.
Conceptually identical to desktop TUN — layer-3 packet fd.

| Desktop TUN | Android VpnService |
|---|---|
| Routes set via OS routing table after TUN comes up | Routes set declaratively on `Builder.addRoute()` BEFORE `establish()` |
| Per-app routing via `process_name` in sing-box rules | Per-app via `Builder.addAllowedApplication(pkg)` / `addDisallowedApplication(pkg)`, taken at establish time, **immutable while tunnel is up** |
| Multi-tunnel possible | One active VpnService per device |
| User invokes elevation (UAC / sudo / pkexec) | Single OS consent dialog the first time per app+package |
| Tunnel survives app lifetime | Killed when app's process killed unless run as foreground service |
| No notification required | **Mandatory** persistent notification while VpnService is active |

### Load-bearing risk: per-app routing

Desktop story is "Discord.exe goes through VPN, Chrome goes direct." Android
equivalent is package-name filtering at tunnel establish time:

```kotlin
val builder = Builder()
    .addAddress("172.19.0.1", 30)
    .addRoute("0.0.0.0", 0)              // capture all v4
    .addRoute("::", 0)                    // capture all v6
    .addAllowedApplication("com.discord") // only Discord through VPN
    .setMtu(1500)
val tunFd = builder.establish()
```

**Two consequences**:

1. Changing the app list = rebuild the tunnel = brief disconnect. Our
   desktop UX (toggle live, ETW notices, sing-box hot-reloads) does not
   exist on Android. Document the "1-second blip when changing apps" UX.
2. No process tree, no regex patterns, no `scan_patterns`. Each Android app
   is one package_id. Discord on Android = `com.discord`. We need a
   parallel `Profile.android_packages[]: List<string>` field.

### Wrapping sing-box inside VpnService

Solved by `sing-box-for-android` (Apache 2, Kotlin) — builds Go core into
`libbox.aar` with `with_android_tunhandler`. Their `BoxService.kt` is the
shim we'd reuse. No fork/rewrite of sing-box.

Build flow (we'd consume their AAR):
```
gomobile bind -tags 'with_quic,with_utls,with_clash_api,with_android_tunhandler' \
  -target=android -androidapi 21 -o libbox.aar github.com/sagernet/sing-box/experimental/libbox
```

Output: ~30 MB AAR with Go runtime + sing-box compiled to `libbox.so` for
arm64-v8a, armeabi-v7a, x86_64.

API surface:
- `Libbox.newService(configJson, platformInterface)` — spawn sing-box
  service backed by Go runtime
- `service.start()` / `service.close()` — lifecycle
- `service.commandClient(...)` — equivalent of Clash API
- `Libbox.setMemoryLimit(...)` — Android needs this
- `Libbox.formatConfig(json)` / `Libbox.checkConfig(json)` — same as
  `sing-box check`

### Required Android manifest

```xml
<service
    android:name=".VpnRouterService"
    android:permission="android.permission.BIND_VPN_SERVICE"
    android:foregroundServiceType="systemExempted"
    android:exported="false">
    <intent-filter>
        <action android:name="android.net.VpnService" />
    </intent-filter>
    <meta-data
        android:name="android.net.VpnService.SUPPORTS_ALWAYS_ON"
        android:value="true" />
</service>
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
<uses-permission android:name="android.permission.QUERY_ALL_PACKAGES"
                 tools:ignore="QueryAllPackagesPermission" />
<uses-permission android:name="android.permission.INTERNET" />
```

`QUERY_ALL_PACKAGES` needed for the apps-list UI. Google Play has a special
review (VPN apps explicitly whitelisted). F-Droid / direct APK
unrestricted.

---

## UI framework decision matrix

| Option | Code reuse | Android-native feel | Risk | Solo cost |
|---|---|---|---|---|
| **A. Avalonia 11.3 Android target** | ~80% XAML + 100% Core | 6/10 — works but feels "desktop ported" | Medium — Avalonia Android less battle-tested | 4-6 wks |
| **B. .NET MAUI Android** | 100% Core, 0% UI (rewrite) | 9/10 — first-class Android, Material 3 | Medium — MAUI rocky historically; 9.0+ much better | 8-10 wks |
| **C. Native Kotlin/Compose** | 100% Core via JNI bindings (or rewrite) | 10/10 — Material You, Studio tooling | Hardest — .NET 8 mobile bindings nascent or fork Core into Kotlin | 12-16+ wks |
| **D. Fork sing-box-for-android** | 0% Core, 0% UI | 10/10 — already idiomatic Android | Brand mismatch + diverge from upstream | 1-2 wks MVP, 4-6 wks for Free Configs parity |
| **E. Flutter wrapping libbox** | 0% Core (Dart rewrite or FFI) | 8/10 | Polyglot — Dart added to C#/Go/Kotlin stack | 8-12 wks |

### Recommendation: **Option A (Avalonia-Android)**

Rationale:
- Already proved Avalonia 11.3 across Win/Mac/Linux with single XAML —
  adding `android` is smallest delta.
- VPNRouter.Core is the asset; reusing it is the whole point. Option B/C
  reuse Core but B costs 60% UI rewrite, C is 4× the work.
- Avalonia Android maturity is biggest unknown; de-risk with spike (1-2
  days).
- Brand consistency: same XAML, design tokens, locale strings.

**Fall-back if spike fails**: Option D (fork sing-box-for-android, layer
in our subscription URL + Free Configs aggregator). Ship the brand, eat
the rewrite.

---

## Code reuse — file-by-file

### From `VPNRouter.Core` (reuse 1:1, ~80%)

| File | Android |
|---|---|
| `AppVersion.cs` | unchanged |
| `Models/*.cs` | unchanged |
| `Services/VlessUriParser.cs` | unchanged |
| `Services/SubscriptionFetcher.cs` | unchanged |
| `Services/SubscriptionResolver.cs` | unchanged |
| `Services/VlessServersResolver.cs` | unchanged |
| `Services/ConfigGenerator.cs` | minor — Android branch in `BuildInbounds` for TUN settings (different default tun name "tun0", `stack="system"`); no `process_name` rules (those become `Builder.addAllowedApplication` at the Kotlin shim layer) |
| `Services/CustomConfigInjector.cs` | unchanged |
| `Services/LeakProtection.cs` | unchanged |
| `Services/FreeConfigs/*.cs` | unchanged (HTTP, JSON, SQLite-free cache; all .NET 8 portable) |
| `Services/ProfileManager.cs` | unchanged |
| `Services/SettingsLoader.cs` / `SettingsMigrator.cs` | unchanged (YamlDotNet works on Android) |
| `Services/UpdateChecker.cs` | add `-android` PlatformSuffix; ApplyUpdate skipped on Play, optional on F-Droid |

### From `VPNRouter.Core` (replace with Android impl)

| File | Android replacement |
|---|---|
| `Services/SingBoxManager.cs` | **Replaced** — Android has no sing-box.exe; instead `AndroidSingBoxRuntime` calls `Libbox.newService(configJson, platformInterface)`. Same public surface (`Start`, `Stop`, `IsRunning`, `Restart`, `IsHealthy`, `Crashed` event), different innards. |
| `Services/HealthMonitor.cs` | keep (talks Clash API on `127.0.0.1:9090` same as desktop) |
| `Services/DnsFlusher.cs` | replace with no-op (Android kernel flushes on tunnel up) |
| `Services/HostsManager.cs` | skip — `/etc/hosts` not writable on non-rooted Android |
| `Services/NetworkInterfaceDetector.cs` | trivial Android version using `ConnectivityManager` |
| `Platform/macOS/MacProcessScanner.cs` etc. | new `Platform/Android/AndroidPackageScanner.cs` — uses Android `PackageManager` |
| `Platform/PlatformServices.cs` | add `#elif ANDROID` branch |

### From `VPNRouter.Core` (exclude on Android — `#if !ANDROID`)

- `Services/EtwProcessMonitor.cs` (Windows-only)
- `Services/FirewallManager.cs` (Windows-only)
- `Services/WindowsDnsHardening.cs` (Windows-only)
- `Services/Zapret*.cs` (out of scope for Android v1)
- `Services/TgProxy*.cs` (Windows-only)
- `Services/OrphanCleanup.cs` (Windows Service deps)
- `Services/CrashReporter.cs` (uses `%TEMP%`, port later)

### From `VPNRouter.App` (Avalonia GUI)

| File | Android |
|---|---|
| `App.axaml` | probably unchanged |
| `Styles/Tokens.axaml` | unchanged |
| `Localization/Strings.cs` | unchanged |
| `Views/MainWindow.axaml` | rework — desktop tabs become bottom-navigation or larger touch targets |
| `Views/Pages/SimplePage.axaml` | mostly unchanged (translates well to phone) |
| `Views/Pages/ServersPage.axaml` | mobile rework |
| `Views/Pages/ApplicationsPage.axaml` | **most rework** — needs `PackageManager.getInstalledApplications` |
| `Views/Pages/FreeConfigsPage.axaml` | mobile rework — master-detail awkward on phone |
| `Views/Pages/DpiBypassPage.axaml`, `TelegramPage.axaml`, `ToolsPage.axaml`, `NetworkPage.axaml` | hide on Android (Zapret/TgProxy Windows-only) |
| `ViewModels/MainWindowViewModel.cs` | ~80% reusable |
| `ViewModels/MainWindowViewModel.SimpleMode.cs` | unchanged |
| `ViewModels/FreeConfigs/*.cs` | unchanged |

### Net code reuse

| Layer | Reuse on Android |
|---|---|
| VPNRouter.Core (excl. Windows-only) | **~85%** |
| VPNRouter.App XAML | **~50%** — pages need touch-friendly layouts |
| VPNRouter.App ViewModels | **~85%** |
| VPNRouter.App design tokens | **100%** |
| VPNRouter.Service | **0%** (no equivalent — VpnService is the Android service) |
| VPNRouter.CLI | **0%** |

---

## Process selection on Android — design

### Schema change in `Profile.cs`

Add Android-side parallel field. Migrate `default.json`:

```jsonc
{
  "name": "Discord_Privacy",
  "description": "Discord voice & chat",
  "processes": [                           // desktop only
    { "name": "Discord.exe", "include_children": true, ... }
  ],
  "android_packages": [                    // NEW
    "com.discord"
  ],
  "dns_mode": "vpn_only",
  "block_on_vpn_fail": true
}
```

Mapping table for existing default profiles:

| Desktop process | Android package |
|---|---|
| Discord.exe | com.discord |
| Telegram.exe | org.telegram.messenger / org.telegram.messenger.web |
| Signal.exe | org.thoughtcrime.securesms |
| WhatsApp.exe | com.whatsapp |
| ChatGPT.exe | com.openai.chatgpt |
| Cursor.exe / claude.exe | (no Android equivalents — exclude) |
| Steam.exe | com.valvesoftware.android.steam.community |
| Spotify.exe | com.spotify.music |
| Slack.exe | com.Slack |

Curated; no auto-derivation from desktop exe names. New entries land in
`default.json` over time.

### "Add custom app" UX

1. List installed apps from `PackageManager.getInstalledApplications(GET_META_DATA)`,
   filter to those with launcher intent.
2. Multi-select grid with app icon + label + package name.
3. Persist into `AppSettings.CustomGroupApps[android]: List<string>`.
4. On Connect, pass union of `profile.android_packages` +
   `customGroupApps["android"]` to `VpnService.Builder.addAllowedApplication`.

### Toggling apps while connected

Tunnel reset (Android API doesn't allow live edit). Options:
- **(a)** Tear down + rebuild — ~1 second blip, simpler. **Pick this.**
- **(b)** Establish with all packages + filter in our packet handler (uid →
  package map). Adds CPU work and uid lookup per packet. Skip v1.

Document.

---

## Distribution — three channels

### 1. Direct APK from `vpn.ninitux.com`

`https://vpn.ninitux.com/android/VPNRouter-v3.0.0.apk` — same staging as
desktop releases. User downloads, allows "install from unknown sources",
done. Works for v1 BETA.

CI: GitHub Actions `dotnet workload install android` + `dotnet publish -f
net8.0-android -p:AndroidPackageFormat=apk` → upload to gh-pages branch
under `/android/`.

### 2. F-Droid

OSS-friendly, automated build from `metadata/com.vpnrouter.android.yml`.
Slow (review queue 1-4 weeks per release). Two options:

- **Official F-Droid** (`f-droid.org`) — submit metadata PR to `fdroiddata`.
- **Self-hosted F-Droid repo** at `vpn.ninitux.com/fdroid/` — build with
  `fdroidserver`, sign with our key. Smaller audience but full control.

Recommend both: official for reach, self-hosted as fast lane.

### 3. Google Play (deferred)

VPN category submission requires:
- Signed AAB (App Bundle).
- `Data safety` form.
- Privacy policy URL (reuse desktop).
- VPN policy compliance: clear consent, no ad tracking, no exfiltration.
- For `QUERY_ALL_PACKAGES` permission: extra justification form. VPN apps
  explicit whitelist.

Realistic timeline: 2-6 weeks first review, 1-3 days subsequent. **Worth
it only after Android UX stable** — every revision burns review cycles.

Skip Play for v1.

---

## MVP scope — v3.0.0-android-beta

### Included

- Avalonia 11.3 Android target + minimal Kotlin/Java shim project for
  `VpnService` (cannot subclass from C# without binding library).
- libbox.aar from `sing-box-for-android` v1.13.x (same sing-box version as
  desktop, 1.13.10).
- Subscription URL + base64 / JSON / VLESS-line parsing — reuse Core.
- Free Configs auto-discovery (14 sources + pool.json fast lane) — reuse
  Core.
- App selection from installed packages — new code, replaces
  ApplicationsPage list source.
- Leak Protection: configured at TUN-builder time (`addRoute("0.0.0.0",
  0)`, `setBlocking(true)`, IPv6 capture).
- Foreground service + persistent notification — Android requirement.
- Always-on VPN support (manifest flag).
- Bilingual Ru/En from existing Strings.cs.

### Excluded (defer to v3.1+)

- Zapret DPI bypass (not feasible without root on Android).
- Telegram proxy (Windows-specific Python embeddable).
- Live app list editing without tunnel restart.
- Per-DNS-server selection UI.
- Tasker / automation intents.
- Wear OS / Android TV variants.
- Auto-updater — F-Droid + Play handle it.

### Acceptance criteria

- [ ] Subscription URL flow works on Pixel 6 (Android 14) and low-end Redmi
      Go (Android 10) — install range API 21+.
- [ ] Free Configs aggregator pulls 14 sources, displays in latency-sorted
      list, connect to one and traffic flows.
- [ ] Toggle Discord into allow-list → connect → Discord traffic tunnels,
      Chrome stays direct (verified via `curl ifconfig.me` from Termux).
- [ ] Tunnel survives screen lock + 30 min idle (Doze).
- [ ] Always-on VPN + "Block connections without VPN" both honoured.
- [ ] Notification persists, tap → opens app.
- [ ] Disconnect → all traffic returns to direct. No leak.
- [ ] APK size < 80 MB (libbox ~30 MB, .NET runtime ~25 MB; rest is XAML).

---

## Effort estimate (solo, FTE weeks)

| Phase | Work | Wks |
|---|---|---|
| **Spike** | Avalonia-Android hello-world running existing XAML on a phone. Verify TextBox / ListBox / theme. Decide go/no-go. | 0.5 |
| **Phase 1: Native shim** | Kotlin module: VpnRouterService extends VpnService. Bind libbox.aar. Bridge Builder.addAllowedApplication / addRoute calls from C# via JNI. | 1.5 |
| **Phase 2: Core integration** | New `AndroidSingBoxRuntime` impl (replaces SingBoxManager). PlatformServices Android branch. AppPaths Android dir. Proven on emulator. | 1.0 |
| **Phase 3: UI port** | MainWindow → mobile shell. ApplicationsPage rewrite (PackageManager). FreeConfigs page mobile layout. Hide Zapret/TgProxy/Service tabs. | 1.5 |
| **Phase 4: Polish** | Notification UX, Always-on, IPv6 leaks, Doze testing, two-device QA. | 0.5 |
| **Phase 5: Distribution** | Direct APK from gh-pages, F-Droid metadata PR, signing key, release notes. | 0.5 |
| **Buffer** | Avalonia-Android surprises, libbox API drift, real-device debugging. | 0.5 |
| **TOTAL** | | **~5-6 weeks beta** |

If spike fails (Avalonia-Android too rough), pivot to Option D: ~3 weeks
for feature parity, but losing brand consistency.

---

## Risks / open questions for spike

1. **Avalonia 11.3 on Android — what breaks?**
   - Status bar inset handling.
   - Soft keyboard / IME (TextBox suffix focus on subscription URL).
   - `<TabControl>` on small screens — switch to BottomNavigation.
   - Dark theme + system theme change.
   - Touch targets — many buttons are 28-32 px tall; needs 48 px minimum.
   - SkiaSharp font cache pressure on low-end devices.
2. **libbox.aar binary size** — ~30 MB across architectures. App Bundle
   splits in Play; for direct APK ship "universal" APK with all ABIs (~80
   MB) or per-ABI APKs (~40 MB each).
3. **Battery / Doze**. sing-box keeps Go scheduler alive; in Doze it gets
   paused. VpnService is exempted while tunnel is active
   (`foregroundServiceType="systemExempted"` API 34+); workaround is
   foreground service notification + partial wake-lock. Standard Android-
   VPN-app dance.
4. **JNI overhead C# → Kotlin → libbox**. Each toggle / status read is one
   IPC. Should be sub-ms; verify on low-end device.
5. **Google Play `QUERY_ALL_PACKAGES`** review — VPN whitelisted but need
   to fill questionnaire. Defer until Play submission.
6. **Banking / anti-cheat apps detecting VpnService** — out of scope; if
   user's bank refuses to run with VPNRouter, that's the bank's policy.
   Document in FAQ.
7. **App icon / branding** — penguin mascot adapts well to Android adaptive
   icon (foreground + background layer). Generate from existing
   `Assets/penguin_mascot.png`.

---

## Concrete next steps

1. **Day 1 — Avalonia-Android spike.**
   - `dotnet new avalonia.app --target-framework net8.0-android` in scratch.
   - Add ProjectReference to VPNRouter.Core (needs `<TargetFrameworks>` mod
     in Core.csproj to multi-target net8.0 + net8.0-android).
   - Render `SimplePage.axaml` on emulator. Note every visual / interaction
     defect.
   - Try `Views/Pages/FreeConfigsPage.axaml` master-detail layout —
     worst-case mobile rendering.
   - Decide: go/no-go.
2. **Day 2-3 — libbox spike (parallel).**
   - Clone `sagernet/sing-box-for-android`.
   - Build libbox.aar locally (verify Go toolchain, gomobile bind).
   - Make 50-line Kotlin Activity that loads our test config.json
     (subscription-mode generated config from desktop) and starts a tunnel.
     Confirm packets flow.
3. **Week 1 — write `plans/vpnrouter-v3.0-android-beta.md`** with detailed
   task breakdown.
4. **Week 2-6 — execute** per Phase 1-5 above.
5. **Week 6 — beta release.** Tag `v3.0.0-android-beta`. Direct APK on
   `vpn.ninitux.com/android/`. F-Droid metadata PR opened.
6. **Post-beta — Play submission discussion.** Only after Android UX
   proven on real devices for stable cycle.

---

## Files most relevant for this port

Critical references:

- `VPNRouter.Core/Platform/PlatformServices.cs`
- `VPNRouter.Core/Services/SingBoxManager.cs`
- `VPNRouter.Core/Services/ConfigGenerator.cs`
- `VPNRouter.Core/AppPaths.cs`
- `VPNRouter.Core/Models/Profile.cs`
- `VPNRouter.App/VPNRouter.App.csproj`
- `VPNRouter.Core/VPNRouter.Core.csproj`
- `profiles/default.json`

External references:
- https://github.com/SagerNet/sing-box-for-android (libbox.aar source)
- https://sing-box.sagernet.org/installation/build-from-source/#build-tags
- https://developer.android.com/develop/connectivity/network-ops/vpn
- https://docs.avaloniaui.net/docs/deployment/android-deploy
- https://f-droid.org/en/docs/Inclusion_Policy/

---

**Status**: research only. No code changes. Decision pending on whether to
start v3.0-android-beta now or after v2.x desktop reaches a quieter
maintenance period.

---

## Phase 0 — COMPLETE ✓ 2026-04-29

User triggered start of implementation immediately after this research
landed ("Можешь релизить и начинать реализацию для android"). Phase 0
laid the foundation and **completed end-to-end with the first signed
APK building**:

### What's done

- **`.NET 8 android workload installed**: Mono runtime + AOT cross-
  compilers for arm, arm64, x86, x64. ~600 MB. Verified via
  `dotnet workload list`.
- **`VPNRouter.Core.csproj` multi-target opt-in**: added
  `<TargetFrameworks Condition="EnableAndroidTarget==true">net8.0;net8.0-android</TargetFrameworks>`.
  Default build (and CI) still compiles only `net8.0`. `dotnet build /p:EnableAndroidTarget=true`
  flips the switch. `PLATFORM_ANDROID` define added for that target.
  `InternalsVisibleTo` on tests scoped to net8.0 only.
- **`VPNRouter.Android/` scaffold project**:
  - `VPNRouter.Android.csproj` — Avalonia 11.3 + ProjectReference to
    Core (with `EnableAndroidTarget=true`), application id
    `com.ninitux.vpnrouter`, multi-RID (arm64/arm/x64/x86), APK package
    format. NOT yet added to solution to avoid breaking `dotnet build VPNRouter.sln`
    for users without Android SDK.
  - `AndroidManifest.xml` — VpnService registration, INTERNET +
    FOREGROUND_SERVICE + POST_NOTIFICATIONS + QUERY_ALL_PACKAGES
    permissions, supports-always-on metadata.
  - `README.md` — install steps for Android SDK + JDK, Phase 1
    breakdown with Kotlin/Java pseudocode.

### Toolchain bring-up (after initial choco failures)

choco install of jdk8 + Temurin 17 both rejected MSIs on this VM — environmental
quirk, not code issue. Workaround that worked: direct download from
adoptium.net + msiexec silent install. Documented for future VMs.

Final stack on this VM:
- Temurin 17 JDK (94 MB, direct adoptium.net MSI)
- Android cmdline-tools 11076708 → sdkmanager → installed:
  - platforms;android-34
  - build-tools;34.0.0
  - platform-tools (~500 MB total)
- ANDROID_HOME = `%LOCALAPPDATA%\Android\Sdk`
- JAVA_HOME = `C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot`
- Both env vars persistent at User scope

### First APK milestone (commit `b59d51d`)

```
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release \
  /p:EnableAndroidTarget=true \
  /p:AndroidSdkDirectory=$ANDROID_HOME \
  /p:JavaSdkDirectory=$JAVA_HOME
```

Output: `VPNRouter.Android/bin/Release/net8.0-android/com.ninitux.vpnrouter-Signed.apk`,
**47.19 MB**, debug-key-signed, single-APK with all RIDs (arm64/arm/x64/x86)
AOT-cross-compiled (89 .NET dlls → .so).

Build time: 2:42 cold first run; ~30s incremental expected.

What's IN the APK:
- Avalonia 11.3 runtime + FluentTheme
- VPNRouter.Core (subscription resolver, free-configs aggregator,
  ConfigGenerator, etc. — same Core that ships on desktop)
- MainActivity stub showing TextBlock "VPNRouter v3.0-android Phase 0"
- All required permissions in AndroidManifest (INTERNET,
  FOREGROUND_SERVICE, POST_NOTIFICATIONS, QUERY_ALL_PACKAGES)

What's NOT in the APK yet (Phase 1+):
- libbox.aar (sing-box Go runtime)
- VpnRouterService.kt (Kotlin VpnService shim)
- Real Avalonia App.axaml (shared with desktop) — Phase 3
- App icon + splash + Material themes

---

## Remaining Phases — backlog

### Phase 1 — VpnService + libbox integration (~1.5 weeks)

**Goal**: APK that establishes a working VPN tunnel through a sing-box
config (subscribe URL → server → tunnel up → traffic flows).

1. **Build libbox.aar from sagernet/sing-box-for-android** matching
   our desktop sing-box version (currently 1.13.10):

   ```bash
   git clone https://github.com/SagerNet/sing-box-for-android
   cd sing-box-for-android
   git checkout v1.13.10  # or appropriate tag
   ./gradlew :app:bundleLibboxAar
   ```

   Drop `libbox.aar` (~30 MB) into `VPNRouter.Android/Lib/libbox.aar`.
   Uncomment the `<AndroidLibrary>` ItemGroup in csproj.

2. **Add VpnRouterService.kt** — Kotlin native shim extending
   `android.net.VpnService`. Bridges Builder → libbox via
   `Libbox.newService(configJson, platformInterface)`. Pattern from
   `sagernet/sing-box-for-android/app/src/main/java/io/nekohasekai/sfa/bg/BoxService.kt`.

   Key responsibilities:
   - `onStartCommand` → build VpnService.Builder, set address +
     route + allowed-applications + MTU → `establish()` → start
     libbox service with config JSON.
   - Foreground notification (mandatory for VpnService API 26+).
   - Crash callbacks → propagate to MainActivity / VPNRouter.Core.
   - Stop intent / always-on disconnect handling.

3. **Add AndroidSingBoxRuntime.cs** in `VPNRouter.Core/Platform/Android/`
   (gated by `#if PLATFORM_ANDROID`). Implements the same public
   surface as desktop's `SingBoxManager`:
   - `Start(string configJson)` — sends start intent to VpnRouterService
   - `Stop()` — sends stop intent
   - `IsRunning` — query VpnService state
   - `Restart` — stop + start
   - `Crashed` event — bridged from Kotlin via JNI callback
   - `IsHealthy()` — Clash API at `127.0.0.1:9090` (libbox exposes
     same endpoint as desktop sing-box)

4. **Wire `VpnEngine.StartAsync` / `Stop` to AndroidSingBoxRuntime
   on PLATFORM_ANDROID** instead of process-spawn-based SingBoxManager.

5. **Generate sing-box config without process_name rules**:
   `ConfigGenerator.cs` already has the per-platform branching
   pattern; add `if (PLATFORM_ANDROID) skip process_name rules`
   block. Apps filtering happens at VpnService.Builder layer
   instead.

6. **Write `Profile.android_packages: List<string>` schema field**.
   Migrate `default-android.json` (similar to existing
   `default-macos.json` / `default-linux.json`):

   | Desktop process | Android package |
   |---|---|
   | Discord.exe | com.discord |
   | Telegram.exe | org.telegram.messenger |
   | WhatsApp.exe | com.whatsapp |
   | Steam.exe | com.valvesoftware.android.steam.community |
   | Spotify.exe | com.spotify.music |
   | (full table in research §"Mapping table" above)

7. **Bridge profile.android_packages → VpnService.Builder.addAllowedApplication()**
   at tunnel-establish time.

8. **Smoke test**: install APK on emulator (or device via adb), enter
   subscription URL, click Connect. Verify:
   - osascript-style consent dialog appears (first run only)
   - VPN icon appears in status bar
   - Notification persists
   - `curl -x ifconfig.me` (from Termux) shows proxy IP
   - Disconnect → traffic returns direct

### Phase 2 — UI shell mobile-friendly (~1.5 weeks)

**Goal**: real Avalonia UI, Material-feeling, touch-friendly, sharing
maximum XAML with desktop App.

1. **Share Tokens.axaml + Localization/Strings.cs** between desktop
   and Android via linked files in csproj:

   ```xml
   <ItemGroup>
     <Compile Include="..\VPNRouter.App\Localization\Strings.cs"
              Link="Localization\Strings.cs" />
     <AvaloniaXaml Include="..\VPNRouter.App\Styles\Tokens.axaml"
                   Link="Styles\Tokens.axaml" />
   </ItemGroup>
   ```

2. **Mobile shell** — replace top-tab navigation with bottom nav
   (Material-style). Phone-first layout:
   ```
   ┌─────────────────────────┐
   │  Status: Connected   ⚙  │  ← top app bar (compact)
   ├─────────────────────────┤
   │                         │
   │      Active page        │  ← content area
   │                         │
   ├─────────────────────────┤
   │ 🏠Home 🌐Servers 📱Apps │  ← bottom nav
   └─────────────────────────┘
   ```

3. **Adapt SimplePage.axaml** to share with Android with `<OnPlatform>`
   tweaks for touch targets (48px minimum), font sizes, spacing.

4. **Rewrite ApplicationsPage.axaml** for Android — instead of
   process-name list, show installed apps via PackageManager
   (icon + label + checkbox). Use `Java.Interop` to query
   `ApplicationInfo` list filtered by launcher intent.

5. **Adapt FreeConfigsPage.axaml** — master-detail at desktop
   becomes sequential pages on phone (list → tap → detail).

6. **Hide Zapret / TgProxy / Service tabs** on Android (Windows-only
   features, already conditionally compiled in Core).

### Phase 3 — Polish (~0.5 weeks)

1. App icon (adaptive icon — foreground penguin mascot + green
   background) generated from existing `Assets/penguin_mascot.png`.
2. Splash screen.
3. Notification UX (action buttons: Disconnect, Switch server).
4. Always-on VPN: manifest already has `supports-always-on=true`;
   verify in Android Settings → Network → VPN → VPNRouter →
   Always-on toggle works.
5. Doze mode / battery optimisation — verify tunnel survives 30 min
   idle screen-off.
6. IPv6 leak protection — `addRoute("::", 0)` in builder.
7. Theme (Light/Dark + Material You where available — API 31+).

### Phase 4 — Cleanup + warnings (~0.5 weeks)

1. Add `#if !PLATFORM_ANDROID` guards on the 20 CA1416 warnings
   we currently have:
   - `Services/ZapretActions.cs` — Registry.CurrentUser
   - `Services/RuntimeStatusDetector.cs` —
     IPGlobalProperties.GetActiveTcpListeners
   - `Services/TgProxyManager.cs` — same TCP listener API
   - `Services/UpdateChecker.cs` — minor null-ref warnings
2. Remove unused references (Mono.Android.dll, etc. that the Avalonia
   Android target pulls in).
3. APK size optimisation — current is ~47 MB unsigned. Targets:
   - <50 MB single APK (acceptable for direct distribution)
   - Per-RID APKs ~15-20 MB each via `<AndroidPackageFormats>aab</AndroidPackageFormats>`
     for Play Store
4. Headless smoke test on `Build Android APK` workflow in GitHub Actions.

### Phase 5 — Distribution (~0.5 weeks)

1. **Direct APK** from `vpn.ninitux.com/android/`:
   - GitHub Actions workflow `.github/workflows/build-android.yml`
     (mirror of desktop .github/workflows/build-windows.yml).
   - Upload APK to gh-pages branch under `/android/`.
   - Add Android download tile to `ninitux-landing` index.html.
2. **F-Droid metadata** — submit PR to fdroiddata repo:
   - `metadata/com.ninitux.vpnrouter.yml` with build recipe.
   - Self-hosted F-Droid repo at `vpn.ninitux.com/fdroid/` as fast lane.
3. **Signing key** — generate Android release keystore, store in
   GitHub repo secrets (NOT in repo).
4. Tag `v3.0.0-android-beta` and ship.

### Phase 6 — Future (post-beta)

- Live app list editing without tunnel restart (filter by uid in
  packet handler, see research §"Tunnel reset" option (b))
- Always-on + start-on-boot
- Per-DNS-server selection UI
- Tasker / automation intents
- Wear OS / Android TV variants
- Google Play submission (after UX stable on real devices for
  ~2 weeks)

---

## Effort estimate update

| Phase | Status | Wks |
|---|---|---|
| Phase 0 — toolchain + first APK | ✅ DONE 2026-04-29 | 0.5 → actual ~3h |
| Phase 1 — VpnService + libbox + tunnel works | TODO | 1.5 |
| Phase 2 — UI shell mobile-friendly | TODO | 1.5 |
| Phase 3 — Polish | TODO | 0.5 |
| Phase 4 — Cleanup + warnings | TODO | 0.5 |
| Phase 5 — Distribution | TODO | 0.5 |
| Buffer | TODO | 0.5 |
| **TOTAL remaining** | | **5 weeks to v3.0.0-android-beta** |

Phase 0 came in significantly under estimate (0.5w → ~3h actual)
because the bulk was tooling + scaffolding, not code. Phase 1 is the
real cost — JNI bridge to libbox, VpnService Kotlin shim, ConfigGenerator
Android branch, profile schema migration. Estimate stands.

## Build cmd reference (for future sessions)

```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot"
$env:Path = "$env:JAVA_HOME\bin;$env:Path"

dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release `
  /p:EnableAndroidTarget=true `
  /p:AndroidSdkDirectory="$env:ANDROID_HOME" `
  /p:JavaSdkDirectory="$env:JAVA_HOME"
```

ANDROID_HOME and JAVA_HOME are persisted at User scope on this VM, so
fresh shell sessions get them automatically.
