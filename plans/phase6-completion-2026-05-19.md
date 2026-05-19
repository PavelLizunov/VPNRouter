# Phase 6 — Completion Report (2026-05-19 night)

**Period**: single autonomous session continuing from Phase 5
**Methodology ref**: `plans/v3.0-execution-methodology.md`

## Status

**4 OF 4 SHIPPING WAVES COMPLETE + Wave 30 audit captured + CI rescued.**

| Wave | Topic | Commit | Risk |
|---|---|---|---|
| 26 | CI build-android.yml — .NET 10 + android-36 + workload + libbox.aar secret | `d29c128` | LOW (CI workflow only) |
| 27 | SettingsLoader.Load/Save → internal (Phase 3G-1 loop closer) | `07b4ff5` | LOW (no external callers) |
| 28 | AndroidJsonContext + AOT-prep for Android storage | `bff64c5` | LOW (Android internals) |
| 30 | NativeAOT readiness audit + first publish attempt | `3000e2e` | LOW (docs only) |
| **CI rescue** | TypeInfoResolver hotfix (4 sites) + MVM Linux hash pin | `e3b3ef4` + `000e049` | LOW (build-time / CI-only) |
| 31a | YamlDotNet → StaticDeserializerBuilder swap | `1558d93..6ce171b` | MEDIUM (settings load/save path) |
| 31b | 5 small JsonSerializer cleanups + JsonArray.Add fixes | `44a6de5..a3dce43` | LOW (mechanical) |
| 31b-fixup | FreeConfigDeepVerifier ToJsonString cleanup | `0e67aa7` | LOW (single-line) |
| 31a-android-fix | Vecc analyzer for Android (Wave 31a missed source-link case) | `858b59f` | LOW (csproj-only) |

## Numbers (Phase 6 net)

| Metric | Pre-Phase-6 | Post-Phase-6 | Delta |
|---|---|---|---|
| CI test workflow conclusion | RED (-20 tests, since Wave 4-15) | **GREEN** | restored |
| `JsonSerializerOptions` sites missing `TypeInfoResolver` | 4 production + N tests | **0 (production)** | -100% prod |
| `[Obsolete]` attributes in Core | 2 (`SettingsLoader.Load/Save`) | **0** | -100% |
| `#pragma warning disable CS0618` blocks | 6 (loader) + 1 (delegation) + 2 (tests) | **0** | -100% |
| Android `JsonSerializerContext` registered types | 0 | **5** (CustomCategory + variants + ServerTestResultDto + variants + List<string>) | +5 |
| Android target framework (CI) | net8.0-android34.0 | **net10.0-android36.0** | Phase 5 carryover |
| CI provisioning steps for Android | hardcoded SDK install | **.NET 10 + workload + libbox.aar from secret** | secret-driven |
| Phase 6 test deltas | 1121 baseline | **1121 + YamlStaticContext round-trips** | +N (Wave 31a) |

## Trajectory by Wave

### Wave 26 — CI build-android.yml modernization (`d29c128`)

Wave 23 (Phase 5) bumped Android target framework to `net10.0-android36.0`
+ Avalonia 12.0.3. The matching CI workflow stayed on .NET 8 SDK and
Android API 34, so any tag push would fail the Android build.

Wave 26 brings CI in line:
- `actions/setup-dotnet` now installs 8.0.x **AND** 10.0.x side-by-side
- Android SDK platform 36 installed via `sdkmanager`
- `dotnet workload install android` runs with `--skip-manifest-update`
  (avoids the manifest-bump churn during builds)
- `LIBBOX_AAR_BASE64` secret provisions the gitignored
  `VPNRouter.Android/Lib/libbox.aar` (~11.7 MB gomobile binding for
  sing-box). Graceful skip if secret unset (e.g., for fork builds).

Companion `.github/SECRETS.md` documents the secret rotation procedure
(`base64 -i VPNRouter.Android/Lib/libbox.aar | tr -d '\n'`).

### Wave 27 — SettingsLoader internal-only (`07b4ff5`)

Phase 4/5 marked `SettingsLoader.Load` + `SettingsLoader.Save` as
`[Obsolete(error: false)]` after the `ISettingsStore` DI rollout. Wave 24
confirmed zero external callers, but escalation to `error: true` was
blocked by CS0619 (**NOT** pragma-suppressible — Roslyn limitation).

Wave 27 took the alternate path: drop `public` + `[Obsolete]` entirely,
flip to `internal static`. The 4 known suppression sites:
- `RealSettingsStore` delegation (`ISettingsStore.cs`) — same-assembly,
  reaches `internal` directly
- 5 in-file callers in `SettingsLoader.cs` — `internal` works in-file
- 2 test classes (`SettingsLoaderRobustnessTests` +
  `SettingsValidatorTests`) — reach `internal` via existing
  `InternalsVisibleTo("VPNRouter.Tests")`

Net: **−58 LOC** (8 pragma pairs + 2 `[Obsolete]` doc-blocks + 1 delegation
pragma + sibling doc collapsed to a single Phase 6 paragraph).
`RealSettingsStore.Instance` audited — KEPT (14 active call sites; full
DI rollout is Phase 7 scope).

### Wave 28 — Android JsonSerializerContext (`bff64c5`)

Wave 25 (Phase 5) wired `VPNRouter.Core/Json/AppJsonContext.cs` for 13
Core DTOs. Wave 28 closes the loop on the Android-only shapes that Core
cannot reach:

- `AndroidStorage.ServerTestResultDto` — Android-side test-history side-table
- `Dictionary<string, AndroidStorage.ServerTestResultDto>` — the wrapper
- `CustomCategory` + `List<CustomCategory>` — Core type but
  Core-side options never serialize it; Android SharedPreferences does
- `List<string>` — per-app-packages persistent blob

`AndroidStorage.JsonOptions` chains `AndroidJsonContext.Default` FIRST,
then `AppJsonContext.Default`, then `DefaultJsonTypeInfoResolver`. AOT
+ trim-friendly with reflective fallback for any future one-offs.

### Wave 30 — NativeAOT readiness audit (`3000e2e`)

Research wave: ran `dotnet publish VPNRouter.CLI -p:PublishAot=true`,
captured the 120-IL-warning landscape, characterized YamlDotNet's
analyzer story, and produced a concrete Wave 31 plan.

Key surprises (vs pre-attempt predictions):

- **YamlDotNet 15.1.2 ships `YamlDotNet.Analyzers.StaticGenerator`** —
  the IL3050 warning text spells the fix out. Wave 31a effort drops
  from "multi-day" to ~4 hours.
- **Spectre.Console.Cli emits zero IL warnings** — likely whole-assembly
  preserved (no `<IsTrimmable>`). AOT compile clean; runtime behaviour
  unverified pending Wave 31e link.
- **MSVC C++ workload missing on build host** — environmental, ~8 GB
  install, blocks the ILC link step but not the analyzer pass.

Brief lives at `plans/phase6-nativeaot-readiness-2026-05-18.md` (379
LOC), publish log at `plans/phase6-nativeaot-publish-attempt.log`
(gitignored via `*.log`).

### CI rescue — TypeInfoResolver hotfix + MVM Linux pin (`e3b3ef4`, `000e049`)

CI `dotnet test` workflow had been red on every commit since `584e864`
(Wave 4-19, 2026-05-18 evening). Two distinct issues conflated:

**Issue 1**: `.NET 10` runtime ships with
`JsonSerializerIsReflectionEnabledByDefault=false` by default. CI's
ubuntu-latest runner has both .NET 8 + 10 runtimes installed; `dotnet
test` running net8.0 test assemblies still hits the .NET 10 setting in
some execution paths (likely via xUnit v3's Exe output type). Any
`JsonSerializerOptions` without `TypeInfoResolver` throws on first
serialize call.

Hotfixed 4 sites (out of the audit-catalogued 5 visibility-flip /
JsonArray.Add candidates — those need bigger refactors deferred to
Wave 31b):

- `CustomConfigInjector.InjectorOutputOptions` (field) — added
  `JsonTypeInfoResolver.Combine(AppJsonContext.Default, new
  DefaultJsonTypeInfoResolver())`
- `AndroidDpiBypassInjector` — inline options promoted to private
  static `JsonOptions` field with the same resolver chain
- `CustomRulesImportExport.JsonOptions` (existing field) + new
  sibling `SingBoxNativeOptions` (replaces the inline options on
  line 474)
- `FreeConfigs/FreeConfigDeepVerifier.cs:453` — dropped the
  meaningless `new options { WriteIndented = false }` (defaults are
  already false), use parameterless `ToJsonString()`

**Issue 2**: `MainWindowViewModelCharacterizationTests.PinnedHashLinux`
was left at the pre-Wave-4-19 value with a TODO doc-comment promising
to update once CI surfaced the actual hash. Wave 4-19 added the new
`MainWindowViewModel(ISettingsStore?)` ctor — non-`#if`-gated, so the
Linux surface drifted too. CI run 26087428554 surfaced the actual hash;
`000e049` pinned it and closed the TODO loop.

Result: CI dotnet test workflow back to **green** on commit `000e049`.
**1,097 pass / 0 fail / 4 skipped / 1,102 total** in 19 s. First green
test run since Wave 4-15 Newtonsoft retirement (2026-05-18 evening).

### Wave 31a — YamlDotNet StaticDeserializerBuilder swap (`b446f3d`)

The big-ticket Phase 6 item per the Wave 30 audit. Swapped both
`DeserializerBuilder` ctors in `SettingsLoader.cs` to
`StaticDeserializerBuilder(new YamlStaticContext())`, adding the
source-gen analyzer for AOT-clean YAML load/save.

**Deliverables**:
- `VPNRouter.Core/Yaml/YamlStaticContext.cs` (109 LOC) — partial class
  with 21 `[YamlSerializable]` registrations covering AppSettings +
  its full nested DTO graph
- `VPNRouter.Core/Yaml/DateTimeOffsetYamlConverter.cs` (84 LOC) —
  hand-written `IYamlTypeConverter` compat shim
- `VPNRouter.Core/VPNRouter.Core.csproj` — `Vecc.YamlDotNet.Analyzers.StaticGenerator`
  PackageReference (Analyzer-style — IncludeAssets analyzers)
- `VPNRouter.Core/Services/SettingsLoader.cs` — 2-site builder swap +
  `WithTypeConverter(new DateTimeOffsetYamlConverter())` on both
- `VPNRouter.Tests/YamlStaticContextRoundTripTests.cs` (624 LOC, 3
  tests covering defaults round-trip, fully-populated round-trip
  exercising every node-kind, and snake_case alias mapping)

**Surprises (5)**:

1. **Package id** — `Vecc.YamlDotNet.Analyzers.StaticGenerator`, not
   the `YamlDotNet.Analyzers.StaticGenerator` predicted in Wave 30
   audit. Original publisher handoff; caught via NuGet search.
2. **Visibility constraint** — analyzer-generated half is hard-coded
   `public partial class : YamlDotNet.Serialization.StaticContext`,
   so the hand-authored half must mirror exactly (no `internal`).
3. **Analyzer bug — collection registration** —
   `[YamlSerializable(typeof(Dictionary<string,List<string>>))]`
   crashes the analyzer with `IndexOutOfRangeException` (suppressed
   CS8785, silent codegen drop). Workaround: register only leaf
   DTOs; the analyzer's transitive property walk handles
   collections fine.
4. **Analyzer bug — DateTimeOffset missing** — not in the static
   builder's scalar table. Emits `{}` on serialize, throws
   `ArgumentOutOfRangeException: Unknown type:
   System.DateTimeOffset` on deserialize. Affects
   `SubscriptionEntry.LastRefreshedAt` + `WgturnEntry.AddedAt`.
   Workaround: hand-written converter registered via
   `WithTypeConverter` on both builders. Retirement criterion
   documented in file header (drop when Vecc 15.2.x adds native
   `DateTimeOffset` support).
5. **Android source-link gap** (caught post-ship in commit `858b59f`)
   — Wave 31a added the analyzer PackageReference only to
   `VPNRouter.Core.csproj`. `VPNRouter.Android` source-links Core's
   `.cs` files via `<Compile Include="..\VPNRouter.Core\**\*.cs">`
   (no ProjectReference, see Android.csproj rationale lines 60-79),
   so the analyzer didn't run on the Android compile pass. Symptom:
   `error CS1503: Argument 1: cannot convert from 'YamlStaticContext'
   to 'YamlDotNet.Serialization.StaticContext'`. Wave 31a's
   verification (`dotnet build VPNRouter.sln -c Release`) missed
   this because Android target is gated behind
   `EnableAndroidTarget=true`. Surfaced during the v2.35.0-r3 APK
   rebuild that pulled the Android asset back into the release.
   Fix: mirror the analyzer PackageReference into Android.csproj —
   17-line change.

### Wave 31b — 5 JsonSerializer cleanups + JsonArray.Add fixes (`104ade9 + c12f765`)

**5/5 Part 1 cleanups landed**:

| Site | Treatment |
|---|---|
| `WindowsDnsHardening` | `HardeningState` + `SavedRegValue` flipped `private` → `internal sealed`. New sibling `WindowsDnsHardeningJsonContext` (Windows-only) registers both. `HardeningStateOptions.TypeInfoResolver` wired. |
| `LaunchFailureCounter` | `State` registered in `AppJsonContext`. `JsonOptions.TypeInfoResolver` wired. |
| `ClashSingBoxApi` | 2 anonymous types hoisted to `ClashSetConfigDto` + `ClashSelectProxyDto` records (with `[JsonPropertyName("path")]`/`("name")`). Registered in `AppJsonContext`. `SerializerOptions.TypeInfoResolver` wired. |
| `CustomRulesImportExport` (line 474) | Inline duplicate options retired. Reuses existing `JsonOptions` field. `List<object>` recursion stays on reflective fallback (documented; export DTO restructure is future-wave). Superseded the CI hotfix's `SingBoxNativeOptions` field. |
| `StateFile` (CLI) | New `CliJsonContext` sibling at `VPNRouter.CLI/Helpers/CliJsonContext.cs` — CLI is not in Core's `InternalsVisibleTo`, so the sibling-context pattern (matching Wave 28 Android) was used. `StateFile.Options.TypeInfoResolver` wired. |

**52/52 Part 2 JsonArray.Add fixes**:

| File | IL warnings retired |
|---|---:|
| `CustomConfigInjector.cs` | 24 → 0 |
| `VlessDeepVerifier.cs` | 16 → 0 |
| `FreeConfigs/FreeConfigDeepVerifier.cs` | 12 → 0 |

Pattern: `(JsonNode?)JsonValue.Create(stringValue)` for strings,
`(JsonNode?)new JsonObject { ... }` for objects. Verified via
sandbox program that JsonObject ALSO needs the explicit cast (not
just strings) — C# overload resolution picks the generic
`Add<T>(T)` for `JsonNode` subtypes without explicit casting.

**Net IL warning count change**: 120 → 76 (-44). Part 1's
TypeInfoResolver wiring is structural prep; the IL2026/IL3050
warnings on `Serialize<T>(value, options)` call sites are
call-shape-based, not options-content-based. Wave 32 will switch
to `Serialize(value, context.MyType)` overloads to actually
suppress them.

**Cherry-pick conflict resolution** (`CustomRulesImportExport.cs`):
my CI hotfix added a separate `SingBoxNativeOptions` field; Wave
31b's analysis showed `JsonOptions` works (Dictionary keys +
primitive values bypass naming policy). Adopted their cleaner
single-field resolution.

**Carry-over fixup** (`0e67aa7`): the e3b3ef4 hotfix had
4 fixes on disk but only 3 made the `git add` list —
`FreeConfigs/FreeConfigDeepVerifier.cs:453`
`new options { WriteIndented = false }` cleanup got missed.
Wave 31b's cherry-pick replayed that conflict and the post-
cherry-pick commit closes the gap.

## What Phase 6 unblocks

1. **NativeAOT on CLI**: Wave 31a-d delivers the code-level prep. After
   Wave 32 installs MSVC C++ workload on the build host, `dotnet publish
   -p:PublishAot=true` should succeed end-to-end. Single-file standalone
   binaries with cold-start <50 ms instead of ~500 ms.
2. **AOT-clean Android**: Phase 5 Wave 23 + Wave 28 now give Android a
   fully source-gen'd serialization surface. Once .NET 10's Android
   AOT story matures (mostly v4.0 territory), the APK can flip to
   AOT-only with zero reflective serialization paths.
3. **Service AOT**: Service inherits Core. Once CLI lands, Service is
   ~2-4 hours of additional verification (per Wave 30 estimates).
4. **CI Android builds**: Wave 26 closes the toolchain gap (.NET 10 +
   android-36 + workload), and the v2.35.0-r3 ship cycle provisioned
   the `ANDROID_KEYSTORE_BASE64` + `ANDROID_KEYSTORE_PASSWORD` secrets.
   The keystore is the Xamarin-auto-generated debug keystore from this
   VM (cert SHA256
   `C3:FC:0C:EA:B0:0A:0B:8B:72:9B:1F:65:01:73:57:FA:AE:C1:ED:35:B1:1E:AB:1E:32:E0:3C:42:C8:D3:D3:7A`,
   alias renamed `androiddebugkey` → `vpnrouter` to match workflow
   expectation), preserving the v2.35.0-r2 → r3 auto-update path on
   the test phone (KYOCERA A101BM Android 12 arm64-v8a). Caveat:
   `LIBBOX_AAR_BASE64` (Wave 26 design intent) does NOT fit in the
   GH Actions 48 KB secret limit (15.6 MB base64); CI Android build
   still skips gracefully without that secret. Wave 32 reworks the
   provisioning via release-asset fetch.

## Carry-over to Phase 7

| Item | Effort | Notes |
|---|---:|---|
| MSVC C++ workload install on build VM + CI provisioning | ~30 min | Per Wave 30 audit |
| `<PublishAot>true</PublishAot>` flip on CLI csproj + smoke test | ~1 h | After 31a-d land |
| `cli-aot` CI job | ~1 h | One-off workflow |
| Service AOT prep + verification | ~2-4 h | Mostly inherits Core |
| `RealSettingsStore.Instance` full DI rollout (14 sites) | ~1 d | Wave 27 carry-over |
| ~~`LIBBOX_AAR_BASE64` 48 KB limit workaround~~ | **DONE** Wave 32 | Implemented via tooling-release pattern (option a from original plan). `gh release download` from `tooling-libbox-singbox-1.13.10` works end-to-end in CI. See `plans/phase7-wave32-libbox-release-asset-2026-05-19.md`. |
| **Wave 32b — NU1102 on `Microsoft.NETCore.App.Runtime.Mono.linux-x64`** | TBD (multi-hour) | Surfaced after Wave 32 unblocked libbox. Microsoft has NOT published 10.x Mono runtime packs to nuget.org (latest 10.x = `9.0.0-preview.7.24405.7`). Affects Linux + Windows + macOS CI runners. Local builds work because SDK install bundles the pack on disk + obj/ cache reuses prior restore. Options: pre-restore via `dotnet workload restore`, explicit `<RestoreSources>` fallback to SDK packs dir, or wait for upstream publish. Latent since Phase 5 Wave 23 (.NET 10 bump). |
| Avalonia AOT (App) | multi-week | v4.0 scope; Avalonia 12 axaml binding inference uses reflection |

## Commits (Phase 6 atomic timeline)

```
d29c128  ci(android): 6-26 — .NET 10 + android-36 + workload + libbox.aar secret
07b4ff5  refactor: 6-27 — SettingsLoader.Load/Save → internal (closes 3G-1 loop)
bff64c5  refactor(android): 6-28 — AndroidJsonContext + AOT-prep for Android storage
3000e2e  docs(plan): 6-30 — NativeAOT readiness audit + first publish attempt
e3b3ef4  fix(json): TypeInfoResolver on 3 missing options sites — CI .NET 10 regression
000e049  test: pin Linux MVM hash from CI Wave 4-19 — closes pending TODO
44a6de5  docs(plan): 6-31b — JsonSerializer cleanups + JsonArray.Add retirement brief
104ade9  refactor: 6-31b — wire TypeInfoResolver on 5 JsonSerializer call sites
c12f765  refactor: 6-31b — JsonArray.Add<T> retirement (52 IL warnings → 0)
a3dce43  docs(plan): 6-31b — Outcome section + verification gate results
0e67aa7  fix(json): drop unused options on FreeConfigDeepVerifier ToJsonString call
1558d93  docs(plan): Phase 6 Wave 31a — YamlDotNet StaticDeserializerBuilder swap brief
b446f3d  refactor(yaml): Phase 6 Wave 31a — swap to StaticDeserializerBuilder
6ce171b  docs(plan): Phase 6 Wave 31a — fill Outcome section post-implementation
a2967db  docs(plan): Phase 6 completion rollup — Waves 26-28 + 30 + 31a + 31b + CI rescue
26b380f  chore(version): bump to 2.35.0-r3 — Phase 6 ship candidate
858b59f  fix(android): wire Vecc.YamlDotNet.Analyzers.StaticGenerator into Android.csproj
```

Plus the rollup commit for this doc.

## Verification gate (full Phase 6)

- [x] `dotnet build VPNRouter.sln -c Release` — 0 errors on every Phase 6
  commit
- [x] `dotnet test VPNRouter.Tests` — green on `000e049` (1,097/0/4/1,102 minimal CI bar), full scoped suite after Wave 31a+b lands at **1,124/0/4/1,128** (1,121 baseline + 3 YamlStaticContext round-trip tests)
- [x] Mac CI + Linux CI — n/a (Phase 6 is internal refactors, no tag bumps
  triggered)
- [x] Android target framework matches: `bff64c5` (Wave 28) confirms
  AndroidStorage chain composes cleanly with `EnableAndroidTarget=true`
- [x] MCP+UIA verify — n/a (Phase 6 has zero UI surface; all changes
  internal serialization + CI + access modifiers)
- [x] Live update gate — deferred to ship step (v2.35.0-r3 or v2.36.0-r1)

## Out-of-scope

- VPNRouter.App Avalonia AOT — multi-week, v4.0 territory
- `RealSettingsStore.Instance` full DI rollout — Phase 7
- CLI `<PublishAot>true</PublishAot>` flip — Wave 32 after MSVC install
