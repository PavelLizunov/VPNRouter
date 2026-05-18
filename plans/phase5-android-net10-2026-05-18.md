# Phase 5 — Android Avalonia 12 upgrade (ph4-android-net10)

**Owner**: Wave 23 agent — best-effort
**Roadmap ref**: Phase 4 deferred ph4-android-net10
**Effort**: 1-2 days (toolchain heavy)
**Risk**: HIGH (multi-step toolchain bump; .NET 10 GA but new for us)

## Why

Wave 12 (Phase 3A) bumped desktop to Avalonia 12.0.3 but explicitly
pinned Android to Avalonia.Android 11.3.12 because Avalonia.Android 12
requires net10.0-android36.0 — our Android target is net8.0-android34.0.

Phase 5 backlog item: complete the Android-side bump so both desktop
and Android run Avalonia 12. Win: 1867% FPS gain on Android too;
NativeAOT 4× startup improvement; unified UI runtime version.

## What

1. **Install .NET 10 SDK**:
   - Latest stable: `10.0.300` (Azure CDN)
   - Side-by-side with existing 8.0.419
   - Adds to `dotnet --list-sdks`

2. **Install Android SDK 36**:
   ```
   sdkmanager "platforms;android-36"
   ```

3. **Install .NET 10 Android workload**:
   ```
   dotnet workload install android --skip-manifest-update
   ```
   (workload version bumps to 36.0.x once .NET 10 SDK is active)

4. **Bump `VPNRouter.Android.csproj`**:
   ```xml
   <TargetFramework>net10.0-android36.0</TargetFramework>
   <PackageReference Include="Avalonia.Android" Version="12.0.3" />
   <PackageReference Include="Avalonia.ReactiveUI" Version="12.0.3" />
   ```
   And remove the Wave 12 "pin 11.3.12 with rationale" comment block.

5. **Build + fix API breaks**:
   - .NET 10 + Avalonia 12 + Android 36 — multi-layered change
   - Expected breaks: AndroidManifest target SDK, Activity API changes,
     Mono Android API renames (`Application.Context` etc.)

6. **Re-pin Android characterization hash** (the
   `AndroidAppSourceSurfaceHashHelper` source-parse hash) if any
   public surface drifts intentionally.

7. **MCP verify on phone** (device 54499112209 is connected via Mac
   USB; adb works via ssh slovn@192.168.0.246 PATH=/opt/homebrew/bin:$PATH).

## How

**Step 1 — Install .NET 10 SDK** (on Windows VM):
```powershell
$tmp = "$env:TEMP\dotnet-install"
mkdir $tmp; cd $tmp
Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile dotnet-install.ps1
./dotnet-install.ps1 -Channel 10.0 -InstallDir "C:\Program Files\dotnet"
```
Then `dotnet --list-sdks` should show both 8.0.x and 10.0.x.

**Step 2 — Install android-36 via sdkmanager**:
```powershell
& "$env:ANDROID_HOME\cmdline-tools\latest\bin\sdkmanager.bat" "platforms;android-36"
```

**Step 3 — Install/update .NET 10 Android workload**:
```powershell
dotnet workload install android  # picks up .NET 10 manifest
```

**Step 4 — Bump csproj**:
- `<TargetFramework>net8.0-android34.0</TargetFramework>` → `net10.0-android36.0`
- Avalonia.Android `11.3.12` → `12.0.3`
- Remove the Wave 12 explicit-pin comment block

**Step 5 — Build**:
```powershell
dotnet build VPNRouter.Android\VPNRouter.Android.csproj -c Release `
  "/p:EnableAndroidTarget=true" `
  "/p:AndroidSdkDirectory=$env:ANDROID_HOME" `
  "/p:JavaSdkDirectory=$env:JAVA_HOME"
```
Fix API breaks one by one. Common Android 36 changes:
- Foreground service type requirements
- BroadcastReceiver register flags (ReceiverFlags.NotExported now required)
- PackageManager permission flow

**Step 6 — Deploy to phone via Mac**:
```bash
scp VPNRouter.Android/bin/Release/net10.0-android36.0/com.ninitux.vpnrouter-Signed.apk slovn@192.168.0.246:/tmp/vpnrouter-net10.apk
ssh slovn@192.168.0.246 'export PATH=/opt/homebrew/bin:$PATH && adb uninstall com.ninitux.vpnrouter && adb install /tmp/vpnrouter-net10.apk'
ssh slovn@192.168.0.246 'export PATH=/opt/homebrew/bin:$PATH && adb shell monkey -p com.ninitux.vpnrouter -c android.intent.category.LAUNCHER 1'
ssh slovn@192.168.0.246 'export PATH=/opt/homebrew/bin:$PATH && adb exec-out screencap -p > /tmp/phone-net10.png'
```
Then scp screenshot back + view.

## Verification gate

- [ ] .NET 10 SDK installed (side-by-side with 8)
- [ ] platforms;android-36 installed
- [ ] .NET 10 android workload installed
- [ ] VPNRouter.Android.csproj bumped to net10.0-android36.0 + Avalonia 12.0.3
- [ ] Build 0 errors (or documented partial with API breaks listed)
- [ ] APK installs on phone + launches cleanly
- [ ] Characterization hash re-pinned if surface drifts
- [ ] MCP screenshot from phone shows working UI

## Best-effort note

If toolchain install fails or API breaks are extensive (>3 hours work),
the agent should:
1. Document each blocker encountered
2. Revert any partial changes
3. Report "BLOCKED on X" with the specific blocker
4. Recommend whether to retry later or split into smaller sub-tasks

## Outcome

**Status**: PASS (full toolchain bump + clean phone launch).

**Wave 23 agent — 2026-05-18, ~1h45m best-effort window.**

### What installed

| Component | Version | Method |
|---|---|---|
| .NET 10 SDK | 10.0.300 (Azure CDN, runtime 10.0.8) | `dotnet-install.ps1 -Channel 10.0 -InstallDir 'C:\Program Files\dotnet'` — side-by-side with 8.0.419, no .NET 8 uninstall |
| Android SDK platform 36 | API 36 r02 | `sdkmanager "platforms;android-36" "build-tools;36.0.0"` |
| Android build-tools 36 | 36.0.0 | (same command) |
| .NET 10 android workload | 36.1.53 / 10.0.100 | `dotnet workload install android` (rolled forward to 10.0.300 default `dotnet`) |

Side-by-side verify: `dotnet --list-sdks` shows both `8.0.419` and `10.0.300`.
Workload list confirms manifest `36.1.53/10.0.100`.

**Note on workload install**: first attempt failed with
`There is not enough space on the disk` (C: was 99% full — 1.4 GB free).
Cleared NuGet HTTP cache (`dotnet nuget locals http-cache -c` → freed
1.1 GB) to reach 16 GB free, then workload install succeeded.

### What bumped (staged, not committed)

- `VPNRouter.Android/VPNRouter.Android.csproj`:
  - `<TargetFramework>net8.0-android</TargetFramework>` → `net10.0-android36.0`
  - Removed `<TargetPlatformVersion>34</TargetPlatformVersion>` (now encoded in TFM)
  - Bumped Avalonia / Avalonia.Android / Avalonia.Themes.Fluent / Avalonia.Fonts.Inter from `11.3.12` → `12.0.3`
  - Bumped Xamarin.AndroidX.Core from `1.13.1.5` → `1.17.0.2` (required transitive dep of Avalonia.Android 12.0.3 → Xamarin.AndroidX.AppCompat 1.7.1.3)
  - Removed the Wave 12 explicit-pin comment block with the v3.0 ph4-android-net10 deferral note
- **New file** `VPNRouter.Android/MainApplication.cs` (52 LOC): `[Application]`-attributed class inheriting `AvaloniaAndroidApplication<AndroidApp>`. Avalonia 12 retired the generic `AvaloniaMainActivity<TApp>` — the Android Application object now hosts the `AppBuilder` and stores the lifetime; non-generic `AvaloniaMainActivity` reads it back via the internal `IAndroidApplication` interface. `CustomizeAppBuilder` (with `.WithInterFont()`) moved here from `MainActivity.cs`.

### What broke + what fixed

| Break | Root cause | Fix |
|---|---|---|
| `MainActivity.cs:65 — AvaloniaMainActivity<AndroidApp>` CS0308 | Avalonia 12 dropped the generic Activity overload | New `MainApplication.cs` + non-generic `AvaloniaMainActivity` base + remove the old `CustomizeAppBuilder` override (now lives in MainApplication) |
| `AndroidApp.axaml.cs:4375 — Gestures.HoldingEvent` CS0122 | Avalonia 12 made `Gestures` class `internal` | `InputElement.HoldingEvent` (same `RoutedEvent` re-exposed publicly) |
| `AndroidApp.FreeConfigs.cs:461 — Gestures.TappedEvent` CS0122 | Same | `InputElement.TappedEvent` |
| `AndroidApp.Tools.cs:328-330 — RadioButton.Checked` CS1061 (×3) | Avalonia 12 collapsed `RadioButton.Checked`/`.Unchecked` into the inherited `ToggleButton.IsCheckedChanged` routed event | Renamed to `.IsCheckedChanged` (handler signature already matched `EventHandler<RoutedEventArgs>`) |

**Java compile errors during the first build attempt** (132 errors in
`VpnRouterService.java` / `AndroidDeepVerifyBox.java` referencing
`Libbox`, `SetupOptions`, `RoutePrefix`) — these were a worktree
provisioning issue, not Avalonia 12 / .NET 10 / Android 36 breaks.
The worktree was missing `VPNRouter.Android/Lib/libbox.aar` (gitignored
private artifact). Copied from main repo (`/c/Project/VPNRouter/VPNRouter.Android/Lib/libbox.aar`,
11.7 MB) → all 132 errors disappeared.

Final build: **0 errors, 107 warnings** (warnings are pre-existing
ZXing binding-generator BG8401/BG8403/CS0114/CS0618 noise + CS1570
XML-comment formatting; none are new from this bump).

### Output

- `bin/Release/net10.0-android36.0/com.ninitux.vpnrouter-Signed.apk` — 85.0 MB
- `bin/Release/net10.0-android36.0/com.ninitux.vpnrouter.apk` — 84.9 MB
- Signed with the existing `vpnrouter.keystore` debug key

Size jumped from the Phase 0 ~47 MB debug APK because Release builds
the full multi-RID Mono runtime (android-arm / arm64 / x64 / x86) per
the `<RuntimeIdentifiers>` declaration. Acceptable for alpha; an AOT
+ trimmed Release would shrink it (Phase 6 follow-up: NativeAOT, see
brief Follow-up section).

### Characterization hash

`AndroidAppCharacterizationTests.AndroidApp_SourceSurface_MatchesPinnedHash`
re-ran on `net8.0` — **PASS** (no re-pin needed). All my AndroidApp*.cs
changes were method-body only (`Gestures.HoldingEvent` →
`InputElement.HoldingEvent`, `Gestures.TappedEvent` →
`InputElement.TappedEvent`, `.Checked` → `.IsCheckedChanged`); zero
member-set drift. `MainApplication.cs` is a sibling file, not part of
the hash scope (which is files matching `AndroidApp*.cs`).

### Phone verification

Device `54499112209` (KYOCERA A101BM Android 12 baseline test phone)
via Mac USB bridge `slovn@192.168.0.246`.

```
adb uninstall com.ninitux.vpnrouter → Success
adb install /tmp/vpnrouter-net10.apk → Streamed Install Success
adb shell monkey -p com.ninitux.vpnrouter -c android.intent.category.LAUNCHER 1
adb shell pidof com.ninitux.vpnrouter → 31894 (process alive)
ActivityTaskManager: Displayed com.ninitux.vpnrouter/.MainActivity: +3s755ms
dumpsys package: versionName=3.0.0-android-alpha, targetSdk=36, minSdk=23
```

Screenshot pulled to `/tmp/phone-w23.png` shows:
- "Virtual Penguin Network" header + penguin mascot
- Status card "Not connected · Traffic goes straight…"
- LaunchFailureCounter safe-mode banner (cosmetic — fresh install, counter
  incremented in OnCreate before MarkStable fires)
- "Config · Mode: manual · all traffic" tappable row
- VPN config TextBox + QR-scan icon button
- "Route through VPN" radio group (Selected apps / All traffic — All
  traffic checked)
- Autostart link card
- "Connect" CTA
- Bottom OS nav bar visible

Logcat sweep for `FATAL`, `AndroidRuntime:E`, `VpnRouter` — **empty**
during launch window. Only unrelated noise (Google Play services
storage-stats warnings, batterysaving package missing, Ads cronet
timeouts — all OS/system, none from VPNRouter).

### Verification gate (per brief) — all checked

- [x] .NET 10 SDK installed (10.0.300 side-by-side with 8.0.419)
- [x] platforms;android-36 installed (also build-tools 36.0.0)
- [x] .NET 10 android workload installed (manifest 36.1.53/10.0.100)
- [x] VPNRouter.Android.csproj bumped to net10.0-android36.0 + Avalonia 12.0.3 (+ AndroidX.Core 1.17.0.2 transitive bump)
- [x] Build 0 errors, 107 pre-existing warnings (Java compile fixed by libbox.aar copy from main)
- [x] APK installs on phone + launches cleanly (PID 31894, Displayed +3s755ms)
- [x] Characterization hash re-pin **NOT required** — method-body only changes
- [x] MCP screenshot from phone shows full Simple-mode UI rendered

### Caveats / integrator notes

1. **libbox.aar not in worktree** — the worktree skeleton was missing
   `VPNRouter.Android/Lib/libbox.aar` (gitignored, ~11.7 MB private
   sing-box binding). I copied it from `main`'s checkout. If the
   integrator rebuilds the worktree from a fresh skeleton, they'll need
   to re-copy `libbox.aar` from `tools/sing-box-upstream` build output
   or main's `VPNRouter.Android/Lib/`. Same caveat already documented
   in `VPNRouter.Android/CLAUDE.md`.

2. **Two new files staged** for integrator commit:
   - `VPNRouter.Android/MainApplication.cs` (new — Avalonia 12 hosting class)
   - `VPNRouter.Android/MainActivity.cs` (modified — non-generic base, doc-comment, CustomizeAppBuilder removed)
   - `VPNRouter.Android/AndroidApp.axaml.cs` (modified — Gestures.HoldingEvent → InputElement.HoldingEvent inside `MakeAppCategoryRow`)
   - `VPNRouter.Android/AndroidApp.FreeConfigs.cs` (modified — Gestures.TappedEvent → InputElement.TappedEvent in `_fcUseButton.AddHandler`)
   - `VPNRouter.Android/AndroidApp.Tools.cs` (modified — RadioButton.Checked → IsCheckedChanged ×3 in Zapret-mode picker wiring)
   - `VPNRouter.Android/VPNRouter.Android.csproj` (modified — TFM, Avalonia 12.0.3, AndroidX.Core 1.17.0.2, comment cleanup)

3. **Wave 12 explicit-pin block deleted** — the comment that said
   "Android stays on Avalonia 11.3.12 / RATIONALE: ...12.0.3 only ships
    net10.0-android36.0" was removed since we're now on
   net10.0-android36.0 + 12.0.3 with no compromise. Per brief step 4.

4. **CI risk**: the GitHub Actions `android.yml` workflow currently
   installs .NET 8 + sets up Android SDK 34. CI will not build this
   change until the workflow is updated to install .NET 10 + Android 36.
   The local Linux/Mac CI workflows for desktop are unaffected (they
   don't build Android). Phase 4's "Android CI is blocked by private
   wgturn-core anyway" caveat still applies — once the wgturn-core gate
   is resolved, the Android CI YAML will need .NET 10/Android 36 in the
   same PR.

5. **App size regression**: 85 MB signed APK vs Phase 0's ~47 MB.
   Release build with multi-RID Mono runtime; Phase 6 NativeAOT
   (per brief Follow-up) is the obvious next compression lever.

6. **`MEMORY.md` Android section update** (per brief Follow-up #3) —
   not done by this agent; left to integrator since MEMORY.md is
   harness-managed and outside the worktree scope.

## Follow-up

- Avalonia 12 Android NativeAOT (separate Phase 6 task, requires
  JsonSerializerContext source-gen)
- Drop the Wave 12 explicit-pin csproj block after this lands
- Update `MEMORY.md` Android section with new toolchain versions
