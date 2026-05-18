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
*(filled by agent)*

## Follow-up

- Avalonia 12 Android NativeAOT (separate Phase 6 task, requires
  JsonSerializerContext source-gen)
- Drop the Wave 12 explicit-pin csproj block after this lands
- Update `MEMORY.md` Android section with new toolchain versions
