# Phase 2 — 2C: `AndroidApp.axaml.cs` split (7,177 LOC → 4 new partials)

**Owner**: Wave 9 (sequential, single agent — no parallelism, characterization safety, Android MCP verify gate)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` Phase 2C; `plans/v3.0-architecture-roadmap.md` §2 "AndroidApp is a god-class"
**Depends on**: Wave 8 (2B MVM split landed — proves the characterization-snapshot pattern works for god-class splits)
**Effort**: 2 days
**Risk**: HIGH (god-file split + Android-specific quirks; Gate 6 mandatory + Gate 5 MCP verify on Android target)

## Why

`VPNRouter.Android/AndroidApp.axaml.cs` is the Android port's god-class: **7,177 LOC** combining Avalonia UI lifecycle, VPN service wiring, NotificationChannel management, sing-box bootstrap, and platform-specific intent handling. It is the Android sibling of `MainWindowViewModel.cs` (Wave 2B), but on Android each concern is more intertwined with platform-specific APIs (Activity result handling, foreground service callbacks, VpnService permissions).

Splitting makes:
- Per-concern navigation (current file is too large to mentally map)
- Faster Android-specific test iteration (right now any Android tweak rebuilds the whole 7,177-LOC class)
- Lower merge-conflict surface for future Android-only features (wgturn UI Phase 1/2)
- Easier comparison with desktop MVM (Phase 3D consolidation candidate — much of AndroidApp's view-state mirrors MVM)

## What

**Step 1**: Characterization snapshot of `AndroidApp.axaml.cs`'s public/internal surface (same helper as Wave 2B's `MainWindowViewModelCharacterizationTests`, reused as `AndroidAppCharacterizationTests`).

**Step 2**: Extract 4 new partial classes by concern:

| New partial | Approx LOC | Concern |
|---|---|---|
| `AndroidApp.VpnLifecycle.cs` | ~1,800 | VpnService start/stop/restart, sing-box process lifecycle, foreground service |
| `AndroidApp.Notifications.cs` | ~1,400 | NotificationChannel, toast surfaces, status badge updates |
| `AndroidApp.Permissions.cs` | ~1,200 | VpnService permission, runtime permission flow, OnActivityResult |
| `AndroidApp.UiBindings.cs` | ~1,800 | Avalonia UI wiring, view-state, command handlers |

Target: main `AndroidApp.axaml.cs` shrinks to ~1,000 LOC (constructor, DI wiring, top-level orchestration).

**Step 3**: Re-run characterization snapshot. Hash MUST match.

**Step 4**: MCP verify on Android emulator or device:
- Build APK: `dotnet build VPNRouter.Android/VPNRouter.Android.csproj -c Release /p:EnableAndroidTarget=true /p:AndroidSdkDirectory=$ANDROID_HOME /p:JavaSdkDirectory=$JAVA_HOME`
- Install via `adb install -r app-Release-Signed.apk`
- Launch via `adb shell am start -n com.ninitux.vpnrouter/.MainActivity`
- Verify: VPN start, VPN stop, profile switch, settings tab
- Screenshot each via `adb exec-out screencap -p`
- PASS/FAIL per scenario

## How

**Step 1 — Characterization snapshot**:
- Add `VPNRouter.Tests/AndroidAppCharacterizationTests.cs` (gated under `#if EnableAndroidTarget` or via test category filter to skip on non-Android builds).
- Reuse `ComputePublicSurfaceHash` helper from Wave 2B's test class.
- Capture initial hash, pin, commit FIRST.

**Step 2 — Extract VpnLifecycle partial** (highest-priority because it's the security-relevant concern):
- Identify all VpnService / sing-box / foreground-service code.
- Move to `AndroidApp.VpnLifecycle.cs` with `public partial class AndroidApp`.
- Build, characterization test, commit.

**Step 3 — Extract Notifications partial** (same process).

**Step 4 — Extract Permissions partial** (same process).

**Step 5 — Extract UiBindings partial** (same process).

**Step 6 — MCP verify on Android**:
- Use existing Android emulator setup OR ask user for physical device.
- Screenshots captured via `adb exec-out screencap`.
- 4 scenarios: VPN start / VPN stop / profile switch / settings open.
- PASS/FAIL per scenario.

**Step 7 — Update `VPNRouter.Android/CLAUDE.md`** with new layout.

## Verification gate

- [ ] Characterization snapshot test committed BEFORE any extraction
- [ ] 4 new partials extracted (one commit each)
- [ ] Main `AndroidApp.axaml.cs` shrinks from 7,177 → ~1,000 LOC
- [ ] Characterization hash matches pre- and post-split
- [ ] **Gate 1**: build with `/p:EnableAndroidTarget=true` 0 errors
- [ ] **Gate 2**: scoped suite stays green
- [ ] **Gate 5 MCP verify**: 4 Android scenarios PASS (screenshots pinned to brief)
- [ ] **Gate 6 characterization diff**: snapshot hash identical pre/post
- [ ] **Gate 4 simplify**: per-partial diff <100 LOC of restructure
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 3D may consolidate `AndroidApp.VpnLifecycle.cs` and `MainWindowViewModel.RuntimeStatus.cs` under a shared `IVpnLifecycleViewModel` interface (cross-platform view-model layer for v3.0 UI parity).
- If `AndroidApp.UiBindings.cs` is the bulk of the work, consider also splitting it by tab (Profiles / Subscriptions / FreeConfigs / Settings) to match the desktop's structure.
- Wgturn Phase 2 Android surface will land on top of `AndroidApp.VpnLifecycle.cs` — making that file's seam clean is a multiplier.
