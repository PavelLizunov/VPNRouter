# Android Advanced — functional test session 2026-05-10

> **Note**: a parallel TEST-RUN-ALL chip (commit `743cba0`) also produced a report at this same path. That chip's worktree could not reach the phone (VirtualBox VM has no USB pass-through). This report supersedes it because the test was actually executed on real hardware via `slovn@192.168.0.246` (Mac mini's `adb` over SSH). Useful bootstrap insight from the other chip: a fresh git worktree needs `cp /c/Project/VPNRouter/VPNRouter.Android/Lib/libbox.aar VPNRouter.Android/Lib/` before `dotnet publish` succeeds — the AAR is gitignored. Worth folding into TEST-RUN-* chip prompts.

**Build under test**: APK from main `765b74c` (POL-1-CARDS + POL-2-TABS landed) — `com.ninitux.vpnrouter-Signed.apk` 68.9 MB, `versionName=3.0.0-android-alpha`.

**Phone**: KYOCERA A101BM (`A101BM`, serial `54499112209`), Android 12, 1080×1920 px / 450 dpi, USB-connected to Mac mini `slovn@192.168.0.246` (adb via SSH).

**Tester**: Claude Code session, automated via `adb shell input tap` / `screencap` / `uiautomator dump`.

**Test asset**: `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee`
(deferred — could not exercise; see blockers).

---

## Outcome

| TEST | Status | Notes |
|---|---|---|
| TEST-0 Pre-flight | PASS | Build, SCP, install, launch all work. App renders Simple page on cold start. |
| TEST-1 Kebab (8 items) | **BLOCKED** | P0 DEFCT-001 — kebab tap crashes app via Avalonia accessibility provider. |
| TEST-2 Servers | **BLOCKED** | Advanced shell only reachable via kebab (current build has no working "Advanced settings ▸" entry on Simple page; see DEFCT-002). |
| TEST-3 Subscribe | **BLOCKED** | Same as TEST-2. |
| TEST-4 Settings | **BLOCKED** | Same as TEST-2. |
| TEST-5 Applications | **BLOCKED** | Same as TEST-2. |
| TEST-6 Tools | **BLOCKED** | Same as TEST-2. |
| TEST-7 Public | **BLOCKED** | Same as TEST-2. |
| TEST-8 End-to-end VPN | **BLOCKED** | Subscription cannot be added (Subscribe tab unreachable). |

**Aggregate verdict**: P0 ship-blocker. Current `765b74c` build is NOT shippable to users — the kebab is a primary navigation control and tapping it crashes the app within ~1 second.

---

## TEST-0 Pre-flight (PASS)

1. APK built on Windows VM: `dotnet publish VPNRouter.Android/VPNRouter.Android.csproj -c Release -p:RuntimeIdentifier=android-arm64 -p:AndroidSdkDirectory=$ANDROID_HOME -p:JavaSdkDirectory=$JAVA_HOME` → `bin/Release/net8.0-android/android-arm64/publish/com.ninitux.vpnrouter-Signed.apk` (68.9 MB).
2. SCP to Mac → `/tmp/vpnrouter-test.apk`.
3. `adb uninstall com.ninitux.vpnrouter` → `Success`. `adb install /tmp/vpnrouter-test.apk` → `Success`.
4. Verified `versionName=3.0.0-android-alpha`, `versionCode=1`, `minSdk=23`, `targetSdk=34`.
5. Launched via `adb shell monkey -p com.ninitux.vpnrouter -c android.intent.category.LAUNCHER 1`. App reaches Simple page in ~3.3 s (`ActivityTaskManager: Displayed ... +3s296ms`).
6. Baseline rendering of Simple page (`01-launch.png`):
   - Header brand row: penguin avatar · "Virtual Penguin Network" · chips VPN/Zapret/TG · ⋮ kebab.
   - "Not connected" status card with `Traffic goes straight — pick a config and start the tunnel.` subtitle.
   - "Config · Mode: manual · all traffic" row.
   - "VPN config" textbox with placeholder `vless://... or https://...` + hint.
   - "Route through VPN" radio group: Selected apps / All traffic (All traffic active).
   - "Autostart — Configure VPN autostart at Windows boot" tile.
   - Connect button (full-width pill).

   All visually matches v2.32.0 desktop slim Simple page (modulo `manual · all traffic` default vs desktop's `manual · split` — accepted divergence per prior session).

---

## DEFCT-001 — P0 Kebab tap crashes app via Avalonia accessibility provider

**Severity**: P0 (ship-blocker).
**Surface**: Simple page kebab `⋮` (top-right header), and any subsequent navigation that re-opens the kebab popup.
**Reproducer**: 1 step.

### Steps to reproduce
1. Cold launch app (`pm clear` + `monkey LAUNCHER` to ensure fresh state).
2. Tap kebab — bounds `[900,200][990,289]`, e.g. `adb shell input tap 945 244`.
3. Observe: app crashes within ~1 second; launcher takes over the screen.

### Stack trace (full)

```
05-10 17:24:02.731 23935 23935 D AndroidRuntime: Shutting down VM
05-10 17:24:02.731 23935 23935 E AndroidRuntime: FATAL EXCEPTION: main
05-10 17:24:02.731 23935 23935 E AndroidRuntime: Process: com.ninitux.vpnrouter, PID: 23935
05-10 17:24:02.731 23935 23935 E AndroidRuntime: android.runtime.JavaProxyThrowable: [System.Reflection.TargetException]: RFLCT_Targ_ITargMismatch
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at System.Reflection.MethodInvokerCommon.ValidateInvokeTarget(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at System.Reflection.RuntimeMethodInfo.Invoke(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at System.Reflection.RuntimePropertyInfo.SetValue(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at System.Reflection.PropertyInfo.SetValue(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at System.Reflection.PropertyInfo.SetValue(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at Avalonia.Android.Automation.ToggleNodeInfoProvider.PopulateNodeInfo(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at Avalonia.Android.AvaloniaAccessHelper.OnPopulateNodeForVirtualView(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at AndroidX.CustomView.Widget.ExploreByTouchHelper.n_OnPopulateNodeForVirtualView_ILandroidx_core_view_accessibility_AccessibilityNodeInfoCompat_(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at Android.Runtime.DynamicMethodNameCounter.7(Unknown Source:0)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at crc6431345fe65afe8d98.AvaloniaAccessHelper.n_onPopulateNodeForVirtualView(Native Method)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at crc6431345fe65afe8d98.AvaloniaAccessHelper.onPopulateNodeForVirtualView(AvaloniaAccessHelper.java:57)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at androidx.customview.widget.ExploreByTouchHelper.createNodeForChild(ExploreByTouchHelper.java:805)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at androidx.customview.widget.ExploreByTouchHelper.obtainAccessibilityNodeInfo(ExploreByTouchHelper.java:725)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at androidx.customview.widget.ExploreByTouchHelper$MyNodeProvider.createAccessibilityNodeInfo(ExploreByTouchHelper.java:1253)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at androidx.core.view.accessibility.AccessibilityNodeProviderCompat$AccessibilityNodeProviderApi19.createAccessibilityNodeInfo(AccessibilityNodeProviderCompat.java:51)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at android.view.AccessibilityInteractionController.populateAccessibilityNodeInfoForView(AccessibilityInteractionController.java:403)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at android.view.AccessibilityInteractionController.findAccessibilityNodeInfoByAccessibilityIdUiThread(AccessibilityInteractionController.java:358)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at android.os.Handler.dispatchMessage(Handler.java:106)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at android.os.Looper.loop(Looper.java:288)
05-10 17:24:02.731 23935 23935 E AndroidRuntime:    at android.app.ActivityThread.main(ActivityThread.java:7885)
```

### Root cause analysis

`Avalonia.Android.Automation.ToggleNodeInfoProvider.PopulateNodeInfo` is Avalonia 11.3's automation peer that exposes Toggle state (Checked/Unchecked) for `IToggleProvider`-implementing controls to Android's AccessibilityNodeInfo. It uses reflection (`PropertyInfo.SetValue`) to set a property on the AccessibilityNodeInfoCompat object. The `RFLCT_Targ_ITargMismatch` exception means the target instance's runtime type does not match the property's declaring type — a reflection target type mismatch.

This is invoked **automatically** by Android's accessibility framework (`AccessibilityInteractionController`) any time a focusable element changes. Crucially, this happens regardless of whether TalkBack / Voice Access / any user-facing accessibility service is enabled — Android core polls for various reasons (focus tracking, animations, gesture detection). On this device, with `accessibility_enabled=0` and `enabled_accessibility_services=null`, the crash still reproduces. So this is **not** limited to users with accessibility on.

The kebab popup contains `MakeSegmentButton`-built segment rows for theme (Light|Dark) and language (RU|EN). They are plain `Avalonia.Controls.Button`, not `ToggleButton`/`CheckBox`. Yet `ToggleNodeInfoProvider` is being invoked on them, which suggests either:
(a) Avalonia 11.3 incorrectly assigns a Toggle automation peer to plain Buttons under some condition (a Avalonia bug — see avalonia/Avalonia issues around `AvaloniaAccessHelper`), OR
(b) Some control elsewhere in the popup tree (a `RadioButton`, `CheckBox`, or `ToggleSwitch`) has a peer with a property type mismatch.

Searching `VPNRouter.Android/AndroidApp.axaml.cs:790-860` (kebab popup construction): the popup contains a `StackPanel` with theme/language segment rows, three diagnostic Buttons, three troubleshooting Buttons, an About row, and an Advanced toggle Button. No `CheckBox`/`ToggleSwitch`/`RadioButton`. So hypothesis (a) is more likely.

### Proposed fix paths (for DEFCT-001 fix chip)

1. **Workaround A — disable accessibility on kebab popup**: set `AutomationProperties.AccessibilityView="Raw"` on the popup's root `Border` (`menuPanel`) so the entire kebab content is hidden from Android's accessibility tree. Trade-off: blind users with TalkBack cannot navigate the kebab — they have to use the visible Advanced settings card path.

2. **Workaround B — disable Avalonia's ToggleNodeInfoProvider for buttons**: subclass `Avalonia.Controls.Button` (or set `AutomationProperties.IsRequiredForForm=false` + clear `AutomationProperties.ItemType`) so Avalonia doesn't attach a Toggle peer.

3. **Upstream fix**: investigate Avalonia source (`Avalonia.Android.Automation.ToggleNodeInfoProvider`) for the reflected property name; check if it's `IsCheckedProperty` being called on a non-`ToggleButton` instance. If yes, file an Avalonia bug + apply a local patch (override `OnInitializeAccessibilityEvent` on our control wrapper).

4. **Replace segment buttons with a dropdown / inline toggle**: avoid the segment-button pattern entirely. Already deferred for desktop parity reasons, so not preferred.

**Recommended**: Workaround A as immediate ship-fix (1-2 LOC on the popup root); pursue (3) as follow-up to restore a11y.

### Evidence
- `plans/test-screenshots-2026-05-10/01-launch.png` — pre-tap baseline.
- `plans/test-screenshots-2026-05-10/02-kebab-open.png` — post-crash (launcher takes over screen).
- Logcat extract above (full crash from `adb logcat -d`).
- Reproduced **twice** independently in this session at `13:22:25.986` and `17:24:02.731` — same stack trace.

---

## DEFCT-002 — P1 Simple page ScrollViewer doesn't respond to swipe gestures

**Severity**: P1 (UX defect — feature partially unreachable).
**Surface**: Simple page main content area.
**Reproducer**: 1 step.

### Steps to reproduce
1. Cold launch app, observe Simple page rendered with vertical scrollbar visible on the right edge of the content area (`bounds=[0,164][1080,1621]`, scrollable=true per uiautomator dump).
2. Swipe up on the content area: `adb shell input swipe 540 1500 540 200 200` (fast) and `adb shell input swipe 540 1100 540 400 800` (slow). Both swipes start inside the ScrollViewer bounds.
3. Observe screencap — page does not scroll; content remains pinned to the original position. Screenshot identical to baseline.

### Impact

The Simple page has more vertical content than fits a 1920-px viewport (visible at the bottom of `01-launch.png`: a partial white card peek under the Connect button). That card is likely the "Advanced settings ▸" CTA referenced in the codebase comment at `VPNRouter.Android/AndroidApp.axaml.cs:838-839`. Without working scroll, this CTA is **unreachable**, leaving the kebab as the only entry to Advanced — which in turn is broken (DEFCT-001).

### Possible root cause

Avalonia 11.3.x has known issues on Android where `ScrollViewer` may not propagate touch gestures through certain `StackPanel`/`Grid` arrangements. The ScrollViewer's `class="ScrollViewer"` and `scrollable="true"` flags are correctly set in the dump, suggesting Avalonia thinks scrolling is enabled but the gesture pipeline isn't wired.

### Proposed fix paths (for DEFCT-002 fix chip)

1. Verify the ScrollViewer's `HorizontalScrollBarVisibility=Disabled` and `VerticalScrollBarVisibility=Auto` settings.
2. Check whether any child of the ScrollViewer is intercepting touch events (e.g., a Grid with `Background=Transparent` and IsHitTestVisible=true).
3. Try wrapping the ScrollViewer's child with `Avalonia.Input.Gestures.IsScrollGestureEnabled="True"`.
4. As a workaround: shrink the Simple page content to fit a single viewport (e.g., remove the bottom Advanced CTA, since users can also reach Advanced via kebab… once DEFCT-001 is fixed).

### Evidence
- `plans/test-screenshots-2026-05-10/04-bottom.xml` (uiautomator dump showing `class="ScrollViewer" scrollable="true"`).
- `plans/test-screenshots-2026-05-10/06-simple-scrolled-hard.png` and `07-slow-scroll.png` — identical to baseline despite swipe gestures.

---

## DEFCT-003 — P3 (info) AccessibilityNodeInfoDumper filters Connect / radios as invisible

**Severity**: P3 (informational; impact unknown without TalkBack user verification).
**Surface**: Simple page accessibility tree.

The uiautomator dump on the Simple page (no kebab open) returns 23566 bytes of XML but does not include nodes for the "Connect" Button, "Selected apps"/"All traffic" radio buttons, or the "Autostart" tile. Logcat shows lines like `AccessibilityNodeInfoDumper: Skipping invisible child:` for these nodes. This means a TalkBack user reading the Simple page may miss these primary controls.

This is downstream of the same Avalonia 11.3 Android automation pipeline issues that cause DEFCT-001. May resolve as a side effect of DEFCT-001 fix.

---

## What testing did NOT cover

Because TEST-1..8 are blocked, the following were **not exercised** this session:

- Kebab functions: theme toggle, language toggle, log viewer, IP-leak check, update check, health check, safe-mode restart, reset settings.
- Advanced shell: Servers list, Custom Config sub-tab, vless/hy2/tuic paste & test.
- Advanced shell: Subscribe tab — including the user-provided test URL `https://ninitux.com/api/v1/app/config/41000af0201dccdfd6acd85bd0e9b6ee`.
- Advanced shell: Settings sub-sections (Routing / Rules / Leak Protection / Content / Updates / Autostart).
- Advanced shell: Applications categories, per-app picker.
- Advanced shell: Tools (Zapret modes, Telegram intent).
- Advanced shell: Public (FreeConfigs Find / Saved / Connect).
- End-to-end VPN flow with real subscription URL.

**Polish work landed on main (`cfef041` POL-2-TABS UniformGrid + `765b74c` POL-1-CARDS token alignment)** is also not visually verified beyond Simple page (Advanced shell tabs are unreachable until DEFCT-001 is fixed).

---

## Recommended next actions

1. **Spawn DEFCT-001 fix chip immediately** (P0 ship-blocker). Apply Workaround A (`AutomationProperties.AccessibilityView="Raw"` on `_kebabPopup` root Border) as quickest unblock, plus pursue an upstream Avalonia investigation in parallel.
2. **Spawn DEFCT-002 fix chip** (P1) — restore Simple page ScrollViewer touch propagation.
3. After both fixes land, **rerun TEST-1..8** — likely a single new chip (consolidated, sequential — same pattern as planned today).
4. Hold any rolling-rN ship until TEST-RUN-ALL passes the kebab + at least TEST-2/TEST-3 (Servers + Subscribe with real URL).

## Process learnings

- Phone access via SSH-to-Mac-mini works end-to-end (build on Windows VM, `scp` to Mac, `adb install` on phone). Round-trip ~30 s for an APK install.
- `screencap` does **not** trigger the accessibility crash (it is a framebuffer dump, not an a11y tree walk). `uiautomator dump` does. Even without explicit dump, Android core polls accessibility on focus changes — so any popup with a buggy peer is risky.
- `adb shell settings put secure accessibility_enabled 0` does not prevent the system-internal a11y polls. So this is not a usable workaround for end-users.
- Fresh git worktree → must `cp /c/Project/VPNRouter/VPNRouter.Android/Lib/libbox.aar VPNRouter.Android/Lib/` before `dotnet publish` or javac fails (gitignored AAR; surfaced by the parallel TEST-RUN-ALL chip 743cba0).

---

**Test session ended at 17:34 (UTC+3) after ~15 min of phone interaction.** All artifacts captured in `plans/test-screenshots-2026-05-10/`.
