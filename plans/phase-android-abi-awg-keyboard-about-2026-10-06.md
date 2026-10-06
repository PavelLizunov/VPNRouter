# Android fixes after stable v2.50.1: APK size, AWG and XHTTP gate, keyboard, About

## Why

After stable v2.50.1 the owner asked, from a tester report with two screenshots, why the Android installer doubled (89.8 to 174.9 MB), why subscriptions still lack the new connection types although the core is new, why the keyboard hides the input on a Samsung phone, whether the kebab menu buttons work and why the version item opens GitHub on the phone while the desktop opens an About window with the core details. The owner then ordered all of it fixed in v2.50.2 and asked that the independent reading is done by the `pi` gpt-sol model, not by more Claude agents.

## Facts checked (2026-10-06, release asset `VPNRouter-v2.50.1-android-arm64.apk`)

- Only Android grew; the Windows, Linux and macOS assets moved by about 0.1 MB.
- The "arm64" APK carries all four ABIs (arm64-v8a, armeabi-v7a, x86, x86_64): native libraries per ABI are 41.1, 41.2, 44.7 and 43.6 MB compressed (libbox.so 22.7, 23.3, 25.2, 24.4 of that), non-native content 4.1 MB. Arm64 plus non-native content is about 45 MB. A trimmed copy of the release APK (other ABIs removed) measured 45.4 MB.
- Cause: `VPNRouter.Android.csproj` set `RuntimeIdentifiers` (plural, four RIDs); the Android SDK 36.1.2 builds the plural list and ignores the `-p:RuntimeIdentifier=android-arm64` of the workflows (`Microsoft.Android.Sdk.AssemblyResolution.targets` lines 78-79 take the plural when it is not empty). The old libbox was arm64 only, so before 2.50.1 only the .NET runtime was multi-ABI (89.8 MB); the new library ships all four ABIs.
- The new libbox.so (stripped) has the tags with_awg and with_xhttp and also with_tailscale, with_openvpn, with_openconnect, with_usbip, which the app does not use (only conflicting-VPN name detectors and Java stubs mention them). Slimming it is a change in the core repository and is out of scope here.
- `SingBoxFeatures.AwgAvailable` and `XhttpAvailable` ask the `sing-box` executable (`sing-box version`, Tags line). Android has no executable (libbox runs in the process), so both stay false: `ServerUriParser.IsSupportedScheme` drops awg://, awg3://, amneziawg:// lines of a subscription, `VlessUriParser` rejects XHTTP links, `ConfigGenerator` and the Free Configs deep verify refuse them. Nothing on Android ever set the overrides.
- Keyboard: `ApplyImeInset` shrinks the page and hides the tab strip and footer; `SizeAdvTabPages` scrolls the focused field into view only inside the Advanced shell tabs. The Home page has no such scroll (screenshot: the config field sits under the keyboard).
- Version item: `AndroidApp.axaml.cs` calls `OnMenuRepoClicked`, which opens the repository page. The desktop `AboutWindow` shows the logo, app version, sing-box version, creator and a repo button.

## What

1. Release APK limited to arm64-v8a: the csproj sets the plural RID list only when no `RuntimeIdentifier` is given (hook and emulator builds keep android-x64); both Android workflows gain a step that fails when the APK carries any other ABI or when libbox.so lacks with_awg or with_xhttp.
2. `SingBoxFeatures.EmbeddedCore` (true on Android): AWG and XHTTP are reported available; tests `SingBoxFeaturesEmbeddedCoreTests`.
3. Keyboard and Subscription tab layout (details after the independent read, see Outcome).
4. In-app About overlay on Android (version, core version from libbox, creator, repo button, Back closes it) and an audit of every kebab menu item.
5. Version `2.50.2-r1` (candidate) after the fixes are verified; stable only on the owner's word.

## Invariants

The mascot art is not touched. No secrets or subscription URLs in commits or logs. Hook and emulator builds are not release artifacts. Naive stays unsupported on Android (legacy AAR). The pinned core stays `v1.14.2-vpnctl.2`.

## Verification plan

Release solution build and the full suite on an authorized worker at the exact SHA; focused tests `SingBoxFeaturesEmbeddedCoreTests`, `ReleaseToolingContractTests`, `PlatformReleaseWorkflowTests`; the PR Android compile check with the new guard step (arm64-only, tags); emulator on `linux-worker` with a hook build for the keyboard, the Subscription tab, the About overlay and each kebab item (screenshots by `screencap`); AWG and XHTTP on Android through the hook (`TEST_SET_CONFIG` with a subscription that contains them) on the x86_64 emulator (the android-x64 hook build carries the x86_64 libbox, as in the 2.50.1 core update); independent read of the diff by `pi` gpt-sol; `bug-hunt`; candidate post-ship gates.

## Unknowns

A real Android phone is not available; the arm64-only release APK itself cannot run on the x86_64 emulator, so its ABI content is proven by the CI guard step and the size, not by running it. Samsung keyboard behaviour is judged from one screenshot.

## Rollback

Revert the PR. A published candidate or stable is never replaced; a correction is a new version.

## Outcome

PR #552; the code of the candidate is `2cabfe0b` (the gates below ran there), the last commit only moves `AppVersion` to `2.50.2-r1` and records this outcome. What changed (about 565 changed lines, 24 files): the Android csproj honours a single `RuntimeIdentifier` (plural list only when none is given); both Android workflows check that the APK has only `arm64-v8a` and that libbox.so carries `with_awg` and `with_xhttp` (contract test `AndroidProject_HonoursSingleRidAndWorkflowsGuardTheApk`); `SingBoxFeatures.EmbeddedCore` opens AWG and XHTTP on Android and makes the health check honest (`SingBoxFeaturesEmbeddedCoreTests`); Home scrolls the focused field above the keyboard; `MainActivity.ImeOverlapPx` counts only the part of the keyboard that still covers the content, keyboard visibility is its own flag; the Subscription tab is one column (list capped at 440 dp, 96 dp minimum); the page root draws the Advanced shell below the log, About, export, import and profile overlays and the menu feedback floats on top; the kebab menu keeps its anchor, is capped to the screen and scrolls; a new in-app About overlay shows the app version, the embedded core version (`LibboxRuntime.coreVersion`) and the author; Export diagnostics opens the share sheet; AmneziaWG rows read `amneziawg`.

Evidence:

| check | result |
|---|---|
| Release solution build on WINBRAT at `2cabfe0b` | 0 errors, 229 warnings (same as v2.50.1) |
| full suite on WINBRAT at `2cabfe0b` | 3369 passed, 0 failed, 19 skipped (v2.50.1: 3364; 5 new tests) |
| PR checks at `2cabfe0b` | all green: test, characterization-windows, go-test-windows, grep, Android compile |
| CI Android compile (unsigned and Signed APK) | `native ABIs: arm64-v8a`, libbox tags include with_awg and with_xhttp |
| release APK size | 174.9 MB before; an arm64-only copy of it 45.4 MB; an android-x64 hook build of this branch 46 MB (before: 167 MB) |
| emulator API 34 (x86_64, `linux-worker`), old release build | keyboard open: page collapsed to a thin strip (double inset); Open log in the Advanced shell shows nothing |
| emulator API 34, this branch | keyboard open: page fills the space above it and the focused config field is fully visible; the Subscription Name and URL fields and Add stay above the keyboard; Open log and Health Check show above the Advanced shell; the health report says the core is built in; About shows version, core 1.14.2-vpnctl.2, author, Repository and Close, Back closes it; Export diagnostics opens the share sheet with the zip; a 14-entry synthetic subscription lists 8 VLESS, 2 XHTTP, 2 amneziawg, 1 hysteria2, 1 tuic rows |
| independent read (`pi` gpt-sol): UI analysis of 15 files and a read of the whole diff | UI analysis: cause of each problem found, patches adapted (not applied verbatim), one false premise corrected (the log overlay was behind the shell); diff read: one P1 hypothesis (ImeOverlapPx could subtract the keyboard twice if the decor view itself shrinks), rejected for stock Android by the emulator run above (overlap 0 in resize mode) and not testable on OEM builds; the first attempt of the read timed out at 25 minutes with high thinking and was repeated with medium thinking |

Not verified: a real Android phone (the tester's Samsung, Android 15+ where the window is not resized by the system), a real AWG or XHTTP tunnel on Android, TalkBack on the menu (the menu keeps its accessibility-hidden state), landscape and large font scale, the share sheet target apps. The ledger line ANDROID-MENU-SUSPECTS keeps the unproven reads of Reset settings, Safe Mode, update check, theme/language rebuild and Test all cancel.

Surprises: the old hook build's emulator run showed that the released app collapses the page under the keyboard below Android 15 (not reported by the tester); the `wm size` change blanks the Avalonia surface on the emulator, `wm density` does not, so the keyboard checks used a larger density; the Android `Localization` already had all About strings (only `AboutVersionUnknown` is new).

Rollback: revert the PR. A published candidate is never replaced; a fix is a new version.
