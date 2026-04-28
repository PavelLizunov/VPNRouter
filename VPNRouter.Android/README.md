# VPNRouter.Android — v3.0 Phase 0 scaffold

Android port of VPNRouter built on **Avalonia 11.3 + libbox.aar**
(sagernet/sing-box-for-android Go runtime via Kotlin shim).

**Status**: scaffold only (csproj, manifest, README). Cannot build yet
on this VM because Android SDK install is blocked (JDK install via choco
fails on this VM session). Continue when SDK + JDK are available.

Reference plan: `plans/vpnrouter-android-research.md` (commit `6dca099`).

## Prerequisites

| Tool | Version | Status |
|---|---|---|
| .NET 8 SDK | 8.0.419 | ✓ installed |
| .NET android workload | 34.0.43 (manifest) | ✓ installed |
| JDK | 17 (Temurin / Microsoft / Adoptium) | ❌ install blocked on VM |
| Android SDK platforms;android-34 | latest | ❌ requires JDK first |
| Android SDK build-tools;34.0.0 | latest | ❌ |
| Android SDK platform-tools | latest | ❌ |

To install Android SDK + JDK:

```powershell
# Option A: Android Studio (heavy, ~3 GB but everything bundled)
choco install androidstudio -y

# Option B: standalone — JDK + cmdline-tools + sdkmanager
choco install temurin17 -y          # if choco mirrors cooperate
# Or download manually:
#   https://adoptium.net/temurin/releases/?version=17 → install MSI
# Then download cmdline-tools:
#   https://developer.android.com/studio#command-line-tools-only
# Extract to %LOCALAPPDATA%\Android\Sdk\cmdline-tools\latest, then:
$env:ANDROID_SDK_ROOT = "$env:LOCALAPPDATA\Android\Sdk"
& "$env:ANDROID_SDK_ROOT\cmdline-tools\latest\bin\sdkmanager.bat" `
    "platforms;android-34" `
    "build-tools;34.0.0" `
    "platform-tools"
```

## Build (after SDK install)

```powershell
# Set ANDROID_SDK_ROOT or pass via msbuild property
$env:ANDROID_SDK_ROOT = "$env:LOCALAPPDATA\Android\Sdk"

# Build:
dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release
```

## Phase 0 (this scaffold)

- `VPNRouter.Android.csproj` — multi-RID project (arm64, arm, x64, x86),
  ApplicationId `com.ninitux.vpnrouter`, references `VPNRouter.Core` with
  `/p:EnableAndroidTarget=true`.
- `AndroidManifest.xml` — VpnService entry, foreground-service
  registration, QUERY_ALL_PACKAGES permission for the apps-list UI.
- This README — capture state + next steps.

**Not in Phase 0**:
- `MainActivity` C# class (Phase 1)
- `VpnRouterService` Kotlin/Java class extending `VpnService` (Phase 1)
- `Lib/libbox.aar` — sing-box for Android (Phase 1)
- Avalonia App.axaml shared with desktop (Phase 3)
- ApplicationsPage rewrite for `PackageManager` (Phase 3)

## Phase 1 — what to add next

1. **Build libbox.aar** from `sagernet/sing-box-for-android` repo.
   Match desktop's sing-box version (currently 1.13.10):

   ```bash
   git clone https://github.com/SagerNet/sing-box-for-android
   cd sing-box-for-android
   git checkout v1.13.10
   gomobile bind -tags 'with_quic,with_utls,with_clash_api,with_android_tunhandler' \
     -target=android -androidapi 21 \
     -o libbox.aar github.com/sagernet/sing-box/experimental/libbox
   ```

   Drop into `VPNRouter.Android/Lib/libbox.aar` and uncomment the
   `<AndroidLibrary>` ItemGroup in csproj.

2. **Add MainActivity.cs** (Avalonia hello-world):

   ```csharp
   [Activity(MainLauncher = true,
             ConfigurationChanges = ConfigChanges.ScreenSize | ...)]
   public class MainActivity : AvaloniaMainActivity<App> { }
   ```

3. **Add VpnRouterService.kt** (Kotlin native shim):

   ```kotlin
   class VpnRouterService : android.net.VpnService() {
       private var libboxService: Service? = null

       override fun onStartCommand(...): Int {
           val builder = Builder()
               .addAddress("172.19.0.1", 30)
               .addRoute("0.0.0.0", 0)
               .addRoute("::", 0)
               .setMtu(1500)
           // ... addAllowedApplication for each package in profile.android_packages

           val tunFd = builder.establish() ?: return START_STICKY
           libboxService = Libbox.newService(configJson, platformInterface)
           libboxService?.start()
           return START_STICKY
       }
   }
   ```

4. **Add AndroidSingBoxRuntime.cs** in VPNRouter.Core (PLATFORM_ANDROID
   branch) replacing SingBoxManager. Uses JNI bridge to call into the
   Kotlin VpnRouterService.

## Phase 2-5

See `plans/vpnrouter-android-research.md` for the full task breakdown
(Phase 2 Core integration, Phase 3 UI port, Phase 4 polish, Phase 5
distribution). Estimated 5-6 solo weeks total.

## Why excluded from solution build right now

`VPNRouter.Android.csproj` is **not** added to `VPNRouter.sln` until
Phase 1 lands. Adding it would break `dotnet build VPNRouter.sln` for
anyone without Android SDK. To work on it locally, `dotnet build` the
csproj directly.

When MainActivity + libbox are wired up and Android SDK is a confirmed
build dependency, add to solution + GitHub Actions matrix.
