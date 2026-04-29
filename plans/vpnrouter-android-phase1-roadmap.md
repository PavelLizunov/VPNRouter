# Android Phase 1 — Working tunnel (next-session roadmap)

**Status (2026-04-29)**: Phase 0 ✅ shipped, basic skeleton for Phase 1
landed in v2.29.0 cycle. APK builds and runs (47 MB signed Phase 0
stub). This doc enumerates what's left for Phase 1 to produce a working
VPN tunnel on Android.

**Reference**: `plans/vpnrouter-android-research.md` Phase 1 section.

---

## What's already in place (v2.29 cycle)

| File | Purpose | State |
|---|---|---|
| `VPNRouter.Android/VPNRouter.Android.csproj` | Avalonia 11.3 csproj, multi-RID, com.ninitux.vpnrouter | ✅ Phase 0 |
| `VPNRouter.Android/MainActivity.cs` | AvaloniaMainActivity entry point | ✅ Phase 0 |
| `VPNRouter.Android/AndroidApp.axaml(.cs)` | Phase 0 stub UI | ✅ Phase 0 |
| `VPNRouter.Android/AndroidManifest.xml` | Permissions + VpnService placeholder | ✅ Phase 0 |
| `VPNRouter.Android/VpnRouterService.kt` | **Kotlin VpnService skeleton** with Intent contract + per-app addAllowedApplication wiring | 🟡 SKELETON — needs libbox.aar to actually start tunnel |
| `VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs` | C# wrapper that sends Intents | 🟡 STUB — Intent dispatch not wired |
| `VPNRouter.Core/Models/Profile.cs` | Added `AndroidPackages: List<string>` field | ✅ |
| `profiles/default-android.json` | 8-category catalog with package IDs | ✅ |

## What's still needed for Phase 1

### Step 1 — Build libbox.aar (1-2 hours, NEEDS GRADLE / ANDROID STUDIO)

Recipe:

```bash
git clone https://github.com/SagerNet/sing-box-for-android
cd sing-box-for-android
git checkout v1.13.10  # match desktop sing-box version (or current upstream)
./gradlew :libbox:bundleLibboxAar
```

Output: `app/libs/libbox.aar` (~30 MB).

Drop into the repo:

```bash
mkdir -p VPNRouter.Android/Lib/
cp /path/to/sing-box-for-android/app/libs/libbox.aar VPNRouter.Android/Lib/libbox.aar
```

Add to `VPNRouter.Android.csproj`:

```xml
<ItemGroup Condition="'$(EnableAndroidTarget)' == 'true'">
  <AndroidLibrary Include="Lib\libbox.aar" Bind="false" />
</ItemGroup>
```

(`Bind="false"` — we use the Java classes directly via Mono.Android
JNI, not the auto-generated C# bindings. Saves ~20 MB binding overhead.)

### Step 2 — Make VpnRouterService.kt buildable (~1 hour)

Currently the file is a placeholder NOT included in the build because:
- VPNRouter.Android.csproj doesn't have a `<AndroidJavaSource>` or
  `<AndroidKotlinSource>` ItemGroup pointing at it.
- The libbox imports are commented out.

Wire-up:
1. Add Kotlin support to the csproj:
   ```xml
   <ItemGroup>
     <AndroidJavaSource Include="VpnRouterService.kt" />
   </ItemGroup>
   ```
   (Avalonia.Android may need extra build props — check current
   Microsoft .NET Android docs at build time.)

2. After libbox.aar is in place (Step 1), uncomment:
   ```kotlin
   import io.nekohasekai.libbox.Libbox
   import io.nekohasekai.libbox.PlatformInterface
   ```

3. Implement the missing TODO at the end of `startTunnel()`:
   ```kotlin
   val tunFd = pfd.fd
   val service = Libbox.newService(pendingConfigJson, MyPlatformInterface(this))
   service.start(tunFd)
   libbox = service
   ```

4. Implement `MyPlatformInterface : PlatformInterface` — this is the
   Kotlin-side glue that libbox calls back into for things like
   "look up DNS" (which we delegate to Android's Resolver) and
   "get current uid" (Android.os.Process.myUid). Reference impl:
   `sagernet/sing-box-for-android/app/src/main/java/io/nekohasekai/sfa/bg/proxy/PlatformInterfaceImpl.kt`.

### Step 3 — Wire AndroidSingBoxRuntime.cs (~1 hour)

Replace the `TODO` markers in
`VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs`:

```csharp
public void Start(string configJson, IReadOnlyList<string> allowedPackages)
{
    var context = global::Android.App.Application.Context;
    var intent = new global::Android.Content.Intent(context, typeof(VpnRouterService))
        .SetAction("com.ninitux.vpnrouter.START")
        .PutExtra("config_json", configJson)
        .PutExtra("allowed_packages", allowedPackages.ToArray());
    context.StartForegroundService(intent);
}

public void Stop()
{
    var context = global::Android.App.Application.Context;
    var intent = new global::Android.Content.Intent(context, typeof(VpnRouterService))
        .SetAction("com.ninitux.vpnrouter.STOP");
    context.StartService(intent);
}

public bool IsRunning()
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var resp = http.GetAsync("http://127.0.0.1:9090/configs").Result;
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}
```

Note: `typeof(VpnRouterService)` requires the Java type to be visible
to Mono.Android. With `Bind="false"` on the AAR, this needs an
explicit binding — `[Register("com/ninitux/vpnrouter/VpnRouterService")]`
on a C# wrapper class, OR re-enable `Bind="true"`.

### Step 4 — VpnService consent flow in MainActivity (~30 min)

Android requires explicit user consent on first VPN connect. Pattern:

```csharp
public void RequestConsentAndStart(string configJson, string[] allowedPackages)
{
    var prepareIntent = global::Android.Net.VpnService.Prepare(this);
    if (prepareIntent != null)
    {
        // First run — system shows VPN consent dialog.
        StartActivityForResult(prepareIntent, REQUEST_VPN_CONSENT);
        // Pending state stored in fields, kicks off in OnActivityResult
        // when consent is granted.
        return;
    }
    // Already consented — start immediately.
    StartTunnelService(configJson, allowedPackages);
}

protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
{
    if (requestCode == REQUEST_VPN_CONSENT && resultCode == Result.Ok)
    {
        StartTunnelService(_pendingConfigJson, _pendingAllowedPackages);
    }
    base.OnActivityResult(requestCode, resultCode, data);
}
```

### Step 5 — VpnEngine branch for PLATFORM_ANDROID (~2 hours)

In `VPNRouter.Core/Services/VpnEngine.cs`, the existing path uses
`SingBoxManager` to spawn `sing-box.exe`. Add an Android branch:

```csharp
#if PLATFORM_ANDROID
    private readonly Platform.Android.AndroidSingBoxRuntime _androidRuntime;
#else
    private readonly SingBoxManager _singBoxManager;
#endif

public async Task StartAsync(/* ... */)
{
    // Existing pre-flight: VlessServersResolver.Resolve, build config, etc.

#if PLATFORM_ANDROID
    var configJson = ConfigGenerator.Serialize(config);
    var pkgs = profile?.AndroidPackages ?? new List<string>();
    _androidRuntime.Start(configJson, pkgs);
#else
    await _singBoxManager.StartAsync(config);
#endif
}
```

ConfigGenerator also needs an Android branch — skip the
`process_name → proxy` route rules when building for Android. Add:

```csharp
#if PLATFORM_ANDROID
    // Android does per-app routing at the OS layer (VpnService.Builder.
    // addAllowedApplication). Skip process_name route rules.
#else
    if (!isFullTunnel && processes.Count > 0)
    {
        // ... existing process_name rule generation ...
    }
#endif
```

### Step 6 — Smoke test (~1 hour)

Manual test on emulator OR physical device:

1. `dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release /p:EnableAndroidTarget=true /p:AndroidSdkDirectory=$ANDROID_HOME /p:JavaSdkDirectory=$JAVA_HOME`
2. `adb install bin/Release/net8.0-android/com.ninitux.vpnrouter-Signed.apk`
3. Launch app on device.
4. Enter subscription URL, tap Connect.
5. System shows VPN consent dialog → tap "OK".
6. Notification appears: "VPNRouter Tunnel active".
7. Open browser on device, visit `https://ifconfig.me` — should show
   the proxy server's IP, not the carrier's IP.
8. Open Termux or curl from another app: `curl -v https://example.com`.
9. Tap notification "Disconnect" or app's Stop button → tunnel down,
   `https://ifconfig.me` shows real IP again.

**If the smoke test passes, Phase 1 is COMPLETE.**

## Estimated effort

| Step | Hours | Notes |
|---|---|---|
| 1. Build libbox.aar | 1-2 | Needs Android Studio / gradle env one-time setup. |
| 2. Make VpnRouterService.kt buildable | 1 | Project plumbing + PlatformInterface impl. |
| 3. Wire AndroidSingBoxRuntime.cs | 1 | C# Intent dispatch. |
| 4. VpnService consent flow | 0.5 | Standard Android pattern. |
| 5. VpnEngine + ConfigGenerator branches | 2 | More than expected — needs care to not regress desktop. |
| 6. Smoke test on device | 1 | Iterate on inevitable bugs. |
| Buffer for unexpected | 2-3 | libbox API drift, Android API quirks, Mono.Android binding gotchas. |
| **TOTAL** | **8-10** | One focused session. |

## Open questions for the implementer

1. **libbox.aar version pinning**: should we commit it to the repo
   (~30 MB binary) or download via a build-time script? Repo commit is
   simpler but bloats clones. Recommendation: download script, with
   the .aar gitignored.

2. **PlatformInterface stub vs full impl**: minimal `PlatformInterface`
   that just no-ops (returns nulls / defaults) might let the tunnel
   start but break DNS / per-app routing. Full impl is ~200 lines.
   Recommendation: full impl from the start, no half-measures.

3. **Per-app routing UX**: how does the user toggle apps in the UI?
   `ApplicationsPage.axaml` currently uses a process-name list. For
   Android we need to query installed apps (`PackageManager.GetInstalledApplications`)
   and present a checkboxed list. That's Phase 2 work, not Phase 1 —
   for Phase 1 we hardcode `default-android.json`'s package list.

4. **Sing-box config compatibility**: desktop ConfigGenerator emits
   1.13.10-format JSON. Does libbox 1.13.10 accept the same format?
   Should be yes (same upstream), but the Phase 1 smoke test will
   confirm.

## Cross-references

- `plans/vpnrouter-android-research.md` — original research doc (Phase
  0-6 overview).
- `plans/vpnrouter-android-phase1-roadmap.md` — this file.
- `VPNRouter.Android/VpnRouterService.kt` — Kotlin skeleton.
- `VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs` — C# stub.
- `profiles/default-android.json` — 8-category catalog.
- Reference impl: `sagernet/sing-box-for-android` (BSD, GPL-3.0
  components — check license compatibility for our F-Droid distribution).
