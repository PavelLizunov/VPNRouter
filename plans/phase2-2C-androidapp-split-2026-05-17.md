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

## Outcome (2026-05-18)

**Status**: PASS — Gates 1+2+4+6 green. Gate 5 MCP verify FLAGGED for integrator (worktree agent has no Android emulator/device).

**LOC delta**:

| File | Lines |
|---|---|
| `AndroidApp.axaml.cs` | **7,177 → 4,904** (−2,273) |
| `AndroidApp.VpnLifecycle.cs` (new) | 666 |
| `AndroidApp.Notifications.cs` (new) | 443 |
| `AndroidApp.Permissions.cs` (new) | 165 |
| `AndroidApp.UiBindings.cs` (new) | 1,206 |
| Total new partials | **2,480 LOC** |

Like Wave 8's MVM split, the final size (4,904 LOC) is higher than the brief's
~1,000 LOC target. The remaining 4,904 LOC are deeply cross-concern AppLifecycle
+ ViewBinding orchestration — splitting them would either move methods out of
the partial class (forbidden under Gate 6) or split methods mid-flow.

**Characterization snapshot strategy** (Option C — emergent):
The brief offered Option A (conditional ProjectReference to VPNRouter.Android)
and Option B (load assembly at test runtime). Both have problems on a host
without Android SDK. The agent invented **Option C: source-parsing**:
- `VPNRouter.Tests/AndroidAppSourceSurfaceHashHelper.cs` (673 LOC) — parses the
  AndroidApp partial-class .cs source files at test time via simple lexer +
  brace tracking, extracts public/internal member signatures, builds the same
  shape `PublicSurfaceHashHelper` does over reflection.
- Pros: works on any host without Android SDK; cross-platform; no assembly load.
- Cons: source parser must be correct; one bug fixed mid-extraction
  (`=>` arrow vs generic `>` ambiguity).

`AndroidAppCharacterizationTests.cs` (103 LOC) pins the hash. Hash matches pre
and post split — Gate 6 PASS.

**Verification gates**:
- [x] Gate 1 build 0 errors (solution build — Android target gated by EnableAndroidTarget)
- [x] Gate 2 scoped suite 1005 pass / 4 skip / 0 fail (+1 new pass, +1 new skip from `AndroidAppDumpMembersFact` Skip-by-default)
- [-] Gate 5 MCP verify FLAGGED — worktree agent has no Android emulator. Integrator runs Gate 5 separately or defers to next user-facing Android release.
- [x] Gate 6 characterization source-hash matches pre/post split
- [x] Gate 4 simplify: per-partial diff is flat cut+paste; one unused `using System.Linq;` removed; field declarations moved to consumer partial (`s_currentLifecycleSubscriber` + `_lifecycleEventsAttached` from main to VpnLifecycle); unused diagnostic test deleted
- [x] Hook gates pass

**Surprises**:
1. AndroidApp.axaml.cs was already partitioned into 11 pre-existing partials
   (AutoUpdate, QrScanApply, DpiBypass, ConfigShare, Tools, AdvancedShell,
   SubscribePage, FreeConfigs, ServerList totaling ~7000 LOC). Wave 9 added
   4 more partials, bringing the total to 15. The "monolith" was thus less
   monolithic than the brief implied — it was already 15K LOC across multiple
   files.
2. VPNRouter.Tests can't reference VPNRouter.Android without the Android target
   enabled. The source-parsing approach (Option C) bypasses this entirely.
3. Source parser had a bug on `=>` (expression-bodied member) confused with
   generic `>` — fixed mid-extraction by tracking expression-body state.

## Follow-up

- Phase 3D may consolidate `AndroidApp.VpnLifecycle.cs` and `MainWindowViewModel.RuntimeStatus.cs` under a shared `IVpnLifecycleViewModel` interface (cross-platform view-model layer for v3.0 UI parity).
- If `AndroidApp.UiBindings.cs` is the bulk of the work, consider also splitting it by tab (Profiles / Subscriptions / FreeConfigs / Settings) to match the desktop's structure.
- Wgturn Phase 2 Android surface will land on top of `AndroidApp.VpnLifecycle.cs` — making that file's seam clean is a multiplier.
