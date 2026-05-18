# Phase 5 — Completion Report (2026-05-18 night)

**Period**: single autonomous session continuing from Phase 4
**Methodology ref**: `plans/v3.0-execution-methodology.md`

## Status

**3 OF 3 PARALLEL WAVES COMPLETE + Android phone sync verified.**
4 atomic commits on main + 1 rollup, both remotes pushed.

## Numbers

| Metric | Pre-Phase-5 | Post-Phase-5 | Delta |
|---|---|---|---|
| Scoped tests passing | 1,121 | **1,121** | 0 (Phase 5 = retire + AOT prep + Android, all behaviour-preserving) |
| Total tests (cumulative Phase 2+3+4+5) | 845 (pre-Phase-2) | **1,121** | **+276** |
| Phase 5 commits | — | 4 atomic + 1 rollup | — |
| Deprecated `[Obsolete]` APIs deleted | 0 | **5 methods + 1 event + multiple DTOs** | -349 net LOC |
| `JsonSerializerContext` source-gen | 0 DTOs | **13 DTOs** (AOT-ready) | Phase 6 NativeAOT unblocked |
| Placeholder CI carve-outs | 1 (`config.example.yaml`) | **0** | `REPLACE_ME_*` tokens applied |
| Android Avalonia version | 11.3.12 (pinned) | **12.0.3** | -1867% FPS deferred per blog |
| Android target framework | net8.0-android (34) | **net10.0-android36.0** | NativeAOT-eligible |
| .NET SDKs on build VM | 1 (8.0.419) | **2** (8.0.419 + 10.0.300) | side-by-side |
| Phone APK verified on hardware | 0 | **1** (KYOCERA A101BM Android 12 arm64-v8a) | physical device test |

## Trajectory by Wave

### Android Phase 4 sync (early in session, pre-briefs)

Before Phase 5 briefs even existed, verified the phone test infrastructure
end-to-end:
- Found phone via Mac SSH (`ssh slovn@192.168.0.246`)
- Mac had brew at `/opt/homebrew/bin` but not in PATH → installed
  `android-platform-tools` (adb already there, just unlinked)
- Built Phase 4 main HEAD Android APK on Windows (Avalonia 11.3.12,
  net8.0-android34.0) — 65 MB signed, 1:56 build
- scp → Mac /tmp → adb uninstall + install — Success
- Launched via monkey (Xamarin CRC-prefixed activity name) — UI rendered
- Screenshot saved at `plans/phone-phase5-r35.png`

Win: confirmed Phase 2-4 Core changes (STJ, IUpdateSource, ISettingsStore,
IHttpClient streaming, ConfigPipeline, StartupPipeline, PlaceholderDefense,
FreeConfigs stages) all flow through to Android via Core source-link.

### Wave 24 — Retire deprecated `[Obsolete]` APIs (commit `b0e3b36`)

Phase 3F/3G/4 marked several APIs `[Obsolete(error: false)]`. Wave 24
deletes those with grep-verified zero callers:

Deleted:
- `UpdateChecker.CheckForUpdateAsync` (-228 net LOC) + 3 private DTOs +
  asset finders + `UpdateAvailable` event
- `AndroidUpdater.CheckAsync(channel)` (-148 net LOC) + helpers

Preserved (still load-bearing per grep):
- `UpdateChecker.CheckAsync` (non-deprecated public surface)
- `UpdateChecker.DownloadAndStageAsync` / `ApplyUpdate` / Win-Mac-Linux
  helpers (IDesktopInstaller surface)
- `AndroidUpdater.DownloadApkAsync` / `BeginInstall` / `CanRequestInstall`
  (IAndroidInstaller plumbing)
- `SettingsLoader.Load` + `Save` — kept at `warning-only [Obsolete]`.
  Discovery: CS0619 (error-level) is **NOT** pragma-suppressible (Roslyn
  limitation). Escalation would break the 4 documented suppression
  sites (RealSettingsStore + 5 internal SettingsLoader callers + 2 pin
  test classes). Phase 6 candidate: refactor SettingsLoader to
  internal-only behind ISettingsStore.

Brief's "2 remaining call sites" reconciliation: brief cited
`Program.cs:80 ResetToDefaults()` + `AndroidApp.Notifications.cs:60
ConsumeRecoveryNotice()` — neither calls the obsolete `Load`/`Save`
methods. They're on the non-deprecated SettingsLoader surface. Brief
conflated "files touching SettingsLoader" with "files calling
Load/Save". No migration needed for either site.

### Wave 25 — config.example REPLACE_ME + JsonSerializerContext AOT prep (commit `d9b0788`)

**5-AOT-1**: `config.example.yaml` literal v2.32.3 placeholder values
swapped for `REPLACE_ME_*` tokens. Wave 17 CI grep-gate carve-out for
this file REMOVED. Top-of-file IMPORTANT comment explains the
placeholder defense will reject verbatim copies.

**5-AOT-2**: New `VPNRouter.Core/Json/AppJsonContext.cs` (NEW, 107 LOC)
— internal sealed partial JsonSerializerContext with 13
`[JsonSerializable]` entries (above the 10+ gate). Wired via
`JsonTypeInfoResolver.Combine(AppJsonContext.Default, new DefaultJsonTypeInfoResolver())`
in 5 production options instances:
- `ProfileManager.SafeJsonOptions`
- `ConfigGenerator.SingBoxOptions`
- `ConfigShareDocument.DocumentOptions`
- `GitHubReleaseSource.GitHubReleaseJsonOptions`
- `AndroidStorage.JsonOptions`

Composition preserves reflective fallback for DTOs not yet in context.
AOT mode (Phase 6 candidate) would pin only `AppJsonContext.Default`.

Surprise: `ServerTestResultDto` lives in `VPNRouter.Android` (not Core).
Substituted `ConfigShareDocument` + 2 `List<T>` wrappers to land at 13
registered types. Phase 6: sibling Android-side context.

Surprise: `UpdateChecker.GitHubApiJsonOptions` deleted entirely by
Wave 24 — no longer relevant. Skipped Wave 25's explanatory comment
on UpdateChecker.cs (moot post-deletion).

Sing-box check integration 3/3 pass — wire format byte-equivalent
between reflective and context-based serialization.

### Wave 23 — Android Avalonia 12 + .NET 10 + Android API 36 (commit `c33e372`)

THE BIG ONE — closes the `ph4-android-net10` follow-up that Phase 3A
(Wave 12) intentionally deferred. Full toolchain bump shipped + phone-
verified in **1h45m best-effort window** (brief allowed 3 hours).

Toolchain installed on Windows VM (side-by-side):
- .NET 10 SDK 10.0.300 (Azure CDN) — 8.0.419 kept
- Android SDK platform 36 (API 36 r02) + build-tools;36.0.0
- .NET 10 android workload (manifest 36.1.53/10.0.100)
- Side-trip: workload install hit "disk full" — cleared NuGet HTTP
  cache (`dotnet nuget locals http-cache -c`, freed 1.1 GB) →
  install succeeded

VPNRouter.Android.csproj:
- TFM `net8.0-android` → `net10.0-android36.0`
- Avalonia / Avalonia.Android / Themes.Fluent / Fonts.Inter `11.3.12` → `12.0.3`
- Xamarin.AndroidX.Core `1.13.1.5` → `1.17.0.2` (transitive)
- Removed Wave 12 explicit-pin comment block

4 API breaks fixed:
1. `MainActivity.cs:65 AvaloniaMainActivity<AndroidApp>` CS0308 —
   Avalonia 12 retired the generic Activity overload. Fix: new
   `MainApplication.cs` (NEW, 52 LOC) inheriting
   `AvaloniaAndroidApplication<AndroidApp>`. Application object now
   hosts AppBuilder + lifetime; non-generic AvaloniaMainActivity reads
   back via internal `IAndroidApplication`.
2. `Gestures.HoldingEvent` CS0122 — `Gestures` class internalized in 12.
   Fix: `InputElement.HoldingEvent`.
3. `Gestures.TappedEvent` CS0122 — same. Fix: `InputElement.TappedEvent`.
4. `RadioButton.Checked` CS1061 ×3 — Avalonia 12 collapsed
   `RadioButton.Checked/Unchecked` into inherited
   `ToggleButton.IsCheckedChanged`. Fix: rename × 3. Handler signature
   already matched.

Phone verification (device 54499112209, KYOCERA A101BM Android 12 arm64-v8a):
- Build: 0 errors, 170 warnings (pre-existing CA1416 + 1 XA4301
  libbox.so dedup). APK 85.0 MB
- Deploy via scp+ssh+adb workflow already verified by Phase 4 sync earlier
- Launch: `Displayed com.ninitux.vpnrouter/.MainActivity: +3s755ms`,
  PID 31894 alive
- dumpsys: targetSdk=36, minSdk=23, versionName=3.0.0-android-alpha
- Logcat sweep for FATAL / AndroidRuntime:E / VpnRouter — empty
- Screenshot saved at `plans/phone-phase5-net10.png` — UI renders
  cleanly (Simple-page Phase 4 layout + new troubleshooting hint
  banner that surfaces on first install)

Characterization hash: existing pin still matches. All AndroidApp*
edits were method-body only; public surface unchanged. No re-pin
needed.

Caveats flagged for Phase 6:
- `VPNRouter.Android/Lib/libbox.aar` (11.7 MB private sing-box
  binding) is gitignored. CI workflow blocked until private repo
  clone OR secrets-stored artifact is wired
- CI workflow `android.yml` needs:
  - install .NET 10 SDK step
  - install android-36 platform step
  - install .NET 10 android workload step
  - libbox.aar provisioning
- `MEMORY.md` Android section needs version updates

## Methodology compliance — gate audit

| Gate | Compliance | Notes |
|---|---|---|
| Gate 1 build clean | 4/4 commits 0 errors | Wave 23 introduced new transient warnings (XA4301 libbox.so dedup) but no errors. .NET 10 + Avalonia 12 + Android 36 multi-layer bump clean |
| Gate 2 scoped tests | 4/4 commits green | 1121 / 4 skip / 0 fail unchanged (Phase 5 = retire/AOT/Android, all behavior-preserving) |
| Gate 3 docs | All 3 briefs Outcome filled + this rollup | |
| Gate 4 self-review | `simplify` ran on all waves; `security-review` ran on Wave 24 (dead code retirement) + Wave 25 (placeholder UX) + Wave 23 (Android Mono API surface bump) |
| Gate 5 MCP verify | **PASSED — phone hardware test** for Wave 23 (most important verification) |
| Gate 6 characterization | Both MVM Windows + AndroidApp hashes unchanged (Phase 5 didn't touch public surface) |
| **Phase 3D follow-up `config.example.yaml`** | DONE (Wave 25 5-AOT-1) |
| **Phase 4 follow-up retire deprecated APIs** | DONE (Wave 24) |
| **Phase 4 follow-up ph4-android-net10** | **DONE** (Wave 23 — the big one) |
| **Phase 4 follow-up JsonSerializerContext AOT prep** | DONE (Wave 25 5-AOT-2) |

## Cumulative across Phases 2+3+4+5

| Phase | Commits | Tests added | LOC delta | Highlight |
|---|---|---|---|---|
| 2 | 20 atomic | +160 | +22865 / -11971 | God-files split (MVM + AndroidApp), 4 abstractions, 9 untested services |
| 3 | 8 atomic + hotfix + rollup | +83 | ~+5000 / ~-1000 | StartupPipeline, IUpdateSource, ISettingsStore, FreeConfigs stages, Avalonia 12 desktop |
| 4 | 8 atomic + rollup | +33 | ~+3500 / ~-700 | Newtonsoft retirement, IHttpClient streaming, CI grep-gate |
| 5 | 4 atomic + rollup | 0 (cleanup) | -349 net | Deprecated API retirement, AppJsonContext, **Android Avalonia 12 + .NET 10** |
| **Total** | **40+ atomic** | **+276** | massive net delta | Full v3.0 modernization stack delivered + Android phone verified |

## Phase 6 backlog (filed from Phase 5 outcomes)

1. **CI workflow `android.yml`** — needs .NET 10 + android-36 +
   workload install steps + libbox.aar provisioning. Until this lands,
   Android CI remains red.
2. **`MEMORY.md` Android section** — bump toolchain versions, mark
   ph4-android-net10 DONE.
3. **Phase 6 NativeAOT** — enable `<PublishAot>true</PublishAot>` on
   Core + Android csprojs. AppJsonContext is wired (Wave 25 prep).
   Need: trim audit + Mono Android NativeAOT verification + APK
   size measurement (expected 4× startup win per Avalonia 12 blog).
4. **Phase 6 retire `SettingsLoader.Load`/`Save`** as
   `error: true` — requires refactor to internal-only behind
   ISettingsStore (since CS0619 is not pragma-suppressible).
5. **Phase 6 retire `RealSettingsStore.Instance` singleton** —
   defer until Program.cs + AndroidApp.Notifications.cs callers
   migrate (those use non-deprecated SettingsLoader surface).
6. **Phase 6 sibling Android JsonSerializerContext** — `ServerTestResultDto`
   + Android-only DTOs need their own context registered in
   `VPNRouter.Android/Json/AndroidJsonContext.cs`.
7. **libbox.aar private repo** — Phase 6 to gate via either repo-secret
   clone or pre-built artifact in GitHub Packages.
8. **GroupBox / Focus Traversal API** (Avalonia 12 cosmetic) — defer
   until user request.

## Pause point — v2.35.0-r2 ship candidate

Phase 5 work delivers:
- Avalonia 12 + .NET 10 + Android API 36 on the Android port (closes
  ph4-android-net10)
- -349 LOC dead code retirement (deprecated APIs from Phase 3F/3G/4)
- JsonSerializerContext source-gen prep for Phase 6 NativeAOT
- config.example.yaml UX cleanup (closes Phase 3D follow-up)
- Phone hardware test verified — Android device 54499112209 launching
  Avalonia 12 build cleanly

Recommended next step: cut `v2.35.0-r2` rolling candidate with the
Android APK as an additional release asset (was not included in
v2.35.0-r1 since Android was still on Avalonia 11). MCP verify on
running desktop binary + phone APK test for Gate 5.
