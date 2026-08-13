# Codebase Reduction & Safe Split — Plan & Execution Contract

> **LOAD-BEARING FILE.** The Goal in section 0 REQUIRES re-opening this file before
> and after every atomic change. Do not work from memory — line numbers and call
> sites here were verified on 2026-06-25 and WILL drift after the first commit;
> always re-grep by symbol before editing.
>
> Provenance: facts below were produced by a read-only inventory (3 agents) and then
> adversarially re-verified by a 5-agent workflow (run `wf_af1156ee-fb4`, 2026-06-25).
> Where the original inventory was wrong, the corrected fact is marked **[verified]**.

---

## 0. GOAL (this is the contract — paste it to start work)

**Goal:** Reduce the VPNRouter codebase and split outlier files, **strictly
behavior-preserving**, by executing the tracks in this file
(`plans/codebase-reduction-and-split-plan.md`).

**Mandatory file-consultation protocol (do NOT work from memory):**
1. **Before any code change** — open this file. Read section 1 (rules) and the
   specific item row in section 3 / 4 / 5 you are about to do.
2. **Re-grep the cited symbols** before editing. Line numbers in this file are a
   2026-06-25 snapshot and drift after prior commits — edit by symbol, never by
   stale line number.
3. **One atomic change for ONE ledger item.** Never mix tracks in a single commit.
   Track 1 (split) and Track 3 (dead code) must NOT share a commit.
4. **Run the blocking workflow** for that change: independent review-agent over the
   diff (briefed cold + project invariants) -> local CI (build + strict lint +
   type-check + unit/contract tests) -> for any UI surface, an MCP/UIA verify that
   the agent actually `Read`s.
5. **Only if everything is green** — commit, push both remotes
   (`git push github HEAD:main && git push origin HEAD:main`), then **re-open this
   file and tick the item's checkbox in section 7** with the commit SHA.
6. **Stop and report** on any red, any ambiguity, or any diff that is not provably
   behavior-preserving. Default to action elsewhere, but here: when unsure, stop.

**Acceptance criteria (global Definition of Done):**
- Every item attempted is ticked in section 7 with a commit SHA, or explicitly
  deferred with a one-line reason.
- Each landed change passed its blocking workflow; `remote main` is green (no red X
  on the commits page — CLAUDE.md rule #11/#15).
- For every split: `git diff` shows **moved members only** (or extracted
  constants/helpers), zero logic edits; the review-agent confirms "pure move".
- `dotnet build VPNRouter.sln -c Release` = 0 errors after each commit.
- Regression suite green (see section 1 for the command); for MainWindowViewModel
  splits the **Windows characterization hash is unchanged** (test passes, not
  re-pinned).
- No public/Core API removed except the one verified-dead member in Track 3, and
  only together with its test edits.

**Per-track acceptance:** see the "Accept when" line on each item below.

**Order of execution:** follow section 6 (safe -> risky). Do not jump to Track 3
before the Track-3 precondition (build-warning pass) is done.

---

## 1. Operating rules (safety rails)

1. **Behavior-preserving only.** No semantic change rides along with a structural
   change. If you find a bug while moving code, file it separately — do not fix it
   in the move commit.
2. **Atomic commits**, one ledger item each, говорящее commit message.
3. **Tracks never mix in one commit.** Especially: a "split" commit must contain no
   deletions of logic; a "dead code" commit must contain no moves.
4. **Blocking workflow per commit** (CLAUDE.md core rule): review-agent + local CI;
   UI changes -> MCP verify. A pre-commit hook enforces it.
5. **Baseline first.** Before the first change: clean `main`, green build + green
   regression suite, work on a branch. Any later regression is then a clear delta.
6. **Dead code only after the build-warning pass** (Track 3 precondition).
7. **Models split MUST preserve `namespace VPNRouter.Core.Models;`** — YAML/JSON
   AOT source-gen (`YamlStaticContext.cs`, `AppJsonContext`) registers types **by
   type, not by file**; a namespace change breaks serialization silently. **[verified]**
8. **Re-grep before edit.** Line numbers here are a snapshot.
9. No emoji in code/config/docs (project rule #9).

**Baseline / regression command (from CLAUDE.md):**
```bash
dotnet build VPNRouter.sln -c Release
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
```
For a fast pre-split smoke (the pinned v2.28.x suite):
```bash
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests"
```

---

## 2. Size map (outliers, threshold = relative to category mean)

| Category | Files | Mean LOC | Outliers (x mean) |
|---|---|---|---|
| App/ViewModels | 27 | 609 | `MainWindowViewModel.cs` 7399 (12x), `FreeConfigsPageViewModel.cs` 1941 (3x) |
| Core/Services | 119 | 366 | `ConfigGenerator.cs` 1785, `SingBoxManager.cs` 1713, `CustomConfigInjector.cs` 1680, `UpdateChecker.cs` 1415 (~5x) |
| Core/Models | 10 | 266 | `AppSettings.cs` 1390 (5x) |
| Android | 38 | 620 | `AndroidApp.axaml.cs` 2384 (already 19 partials) |
| .axaml | 15 | 506 | `NetworkPage.axaml` 2328 (4.6x), `DpiBypassPage.axaml` 875 |

---

## 3. TRACK 1 — Split large files (behavior-preserving)

### T1-A. `VPNRouter.Core/Models/AppSettings.cs` (1390) -> file-per-type
**Risk: low** (was "trivial" — corrected: 4 of 20 types carry behavior). **[verified]**

20 top-level types, all `public class`, none `partial`. The only `partial` token is
the word "partial IPv6" in a comment at line 518. `WgturnEntry` already lives in its
own `Models/WgturnEntry.cs` — exclude it.

Types **with load-bearing behavior** (move whole, keep `System.Linq`/`System.Collections.Generic` usings):
- `VlessConfig` (762-906): `GetEffectiveServers` (818), `GetActiveServers` (861),
  private `BuildAutoSelectPool` (892), non-serialized bool `AutoSelectBestServer`
  (853). Single source of truth for routed servers; covered by `GetEffectiveServersTests`.
  **Needs `using System.Linq;` in its new file.**
- `TunSettings` (1203-1304): `GetEffectiveRouteExcludeAddress` (1275), runtime-only
  `[YamlIgnore][JsonIgnore] AutoDetectedExcludeAddress` (1263).
- `VlessServerEntry` (908-1145): computed `[YamlIgnore][JsonIgnore] IsDnsTunnel` (958).
- `UpdateSettings` (1352-1370): computed `[YamlIgnore] IsExperimental` (1367).

Pure-data POCOs: `AppSettings` (root, keep, 6-117), `EmergencyChannelSettings`
(119-172), `AppConfig` (174-567, biggest), `SubscriptionEntry` (569-598),
`CustomCategory` (600-611), `CustomRule` (613-685), `CustomDirectRule` (687-733),
`CustomConfigEntry` (735-745), `ProfileSource` (747-760), `VlessRealityConfig`
(1147-1168), `VlessTlsConfig` (1170-1188), `VlessTransportConfig` (1190-1201),
`DnsSettings` (1306-1316), `SingBoxSettings` (1318-1335), `MonitoringSettings`
(1337-1350), `UserFreeSource` (1373-1390).

**Proposed split** (minimal-risk default = one file per type; small related POCOs
may be grouped): `AppConfig.cs`, `VlessConfig.cs` (+System.Linq), `VlessServerEntry.cs`,
`TunSettings.cs`, `VlessTransportConfigs.cs` (Reality+Tls+Transport), `CustomRules.cs`
(CustomRule+CustomDirectRule), `EmergencyChannelSettings.cs`, `EngineSettings.cs`
(Dns+SingBox+Monitoring+Update), etc. Keep `AppSettings.cs` as the root only.

**Constraint:** every new file keeps `namespace VPNRouter.Core.Models;` + the right
usings (`System.Text.Json.Serialization`, `YamlDotNet.Serialization`, `System.Linq`
for VlessConfig). `YamlStaticContext.cs:86-106` needs NO change if namespace preserved.
**Do each type-move as its own commit** so a serialization regression bisects cleanly.
**Accept when:** build 0 errors; full test suite green incl. YAML/STJ round-trip
tests (`YamlStaticContextRoundTripTests`, `Phase4StjRoundTripTests`, `GetEffectiveServersTests`);
diff is pure move.

### T1-B. `VPNRouter.Core/Services/SingBoxManager.cs` (1713) -> partials
**Risk: low** (single non-partial class, no siblings). **[verified]**

Second top-level type in the file: `class ProcessMetrics` (1708-1713, DTO returned by
`GetMetrics`) — move to `ProcessMetrics.cs` or leave in anchor. `enum SingBoxState`
at line 9.

**Anchor file `SingBoxManager.cs`** keeps the class decl + ALL fields/props/events +
ctor (217-246) + `Dispose` (1522-1550) + `OnAppDomainProcessExit` (256-260). Reason:
many fields are shared across clusters (see below) — keeping all state in one anchor
makes every partial safe.

Partial files (all `partial class SingBoxManager`):
- `SingBoxManager.Lifecycle.cs`: `Start` (266), `StartWithJson` (269), `Stop` (314),
  `StopInternal` (326-643), `Restart` (645), `LaunchProcess` (1012-1187),
  `HasNetCapability` (928), `TryColocateCronet` (984), `RotateSingBoxLog` (825),
  `WriteJsonToDisk` (1513).
- `SingBoxManager.HotReload.cs`: `ReloadConfig`/`...Json` (693/713),
  `TryReloadConfig`/`...Json` (727/730), `WriteConfigToDisk` (757), `TryHotReload`
  (852), `IsClashApiAlive` (1192) — home it here (also used by Health + LinuxStop;
  partial keeps it reachable).
- `SingBoxManager.Health.cs`: `IsRunning` (763), `IsHealthy` (785), `GetMetrics` (806).
- `SingBoxManager.CrashDetect.cs`: `OnProcessExited` (1205), `LogSingBoxCrashTail`
  (1383), `DetectTunOrphanCrashSignature` (1450).
- `SingBoxManager.LinuxStop.cs`: `LinuxStopEscalationChain` (1581), `TrySpawnAndWait`
  (1649), `IsSingBoxAlive` (1686).

**Shared mutable fields that make this a `partial` (not separate-class) job:**
`_handle` (31, central), `State` (79), stderr ring buffer `_capturedStderr`/`Count`/
`Lock` (101-103, written by Lifecycle, read by CrashDetect), `_restartInProgress`
(140), `_stopInProgress` (178), `LastCrashWasTunOrphan` (204), `_tunLock` (40),
`_http` (77), `_currentConfigPath` (32), events `Crashed`(81)/`Started`(85).
**Accept when:** build 0 errors; SingBox tests green (`SingBoxManagerStateMachineTests`,
`...RestartTunHandshakeTests`, `...CleanupPathTests`); diff is pure move.

### T1-C. `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (7399) -> two more partials
**Risk: low-medium** — guarded by a characterization test. **[verified]**

Existing siblings = **12** (not 10 — docs are stale, see section 8):
AutostartBootstrap, FreeConfigs, Localization, Profiles, ServerTesting, Settings,
SimpleMode, Subscriptions, Wgturn, LocalizedLabels, RuntimeStatus, ConnStats.

**Safety net — characterization test:**
`VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs` +
`PublicSurfaceHashHelper.cs` pin a SHA-256 over ALL members of the type (public +
non-public, `DeclaredOnly`), so **a pure move is provably surface-neutral** and any
accidental rename/retype/drop trips the hash. **Windows pin is STRICT (throws);
Linux/macOS pin is SOFT (writes `.git-suggested-hash-bump.txt`, returns).** So the
hard gate is Windows-only — run the test on Windows.
- Windows hash: `2a7333a5754dddebee5ebca5e466bd32525b7f60766819fc1bd1a4d67a5c93bc`
- Do NOT re-pin the hash; if it changes, the diff was not a pure move — investigate.

`MainWindowViewModel.Banners.cs` <- (all in main file):
`_updateWarningText` (238), `HasUpdateWarning` (240), `DismissUpdateWarning` (243),
`_settingsRecoveryNoticeText` (259), `HasSettingsRecoveryNotice` (261),
`DismissSettingsRecoveryNotice` (264), `_placeholderPruneNoticeText` (278),
`HasPlaceholderPruneNotice` (280), `DismissPlaceholderPruneNotice` (283),
`_conflictingVpnWarningText` (296), `HasConflictingVpnWarning` (298),
`DismissConflictingVpnWarning` (301). **Decision needed:** the conflict-action
members `_lastConflicts` (312), `_skipVpnConflictThisSession` (346),
`RefreshConflictingVpn` (316), `IgnoreVpnConflictAndConnectAsync` (357),
`KillConflictingVpnAsync` (377) — move with the banner or leave. Note:
`ConflictingVpnWarningText` is **assigned** from orchestration in the main file
(323,327,360,438,446,4178,4259) — those write-sites STAY in main (cross-concern
writes to a moved property are fine, same `this`).

`MainWindowViewModel.ThemeAndLogo.cs` <- (in main file): `_isDarkTheme` (137),
`_themePreference` (148), `IsSystemThemePref`/`IsLightThemePref`/`IsDarkThemePref`
(150-152), `_logoLight`/`_logoDark` (163/164), `LogoSource` (171), `LoadAsset` (172),
`TryBuildInvertedLogo` (182), `_themeToggleText` (220). **Note:** the theme
*commands* `SetThemeLight`/`Dark`/`System`/`SetThemePreference` already live in
`MainWindowViewModel.Settings.cs` (480-499) — optionally relocate them to co-locate
(a move between two partials, still hash-neutral). `ApplyTheme` logic ~7252-7366
stays in main.
**Accept when:** build 0 errors; characterization test green on Windows (hash
unchanged); UI MCP verify (theme toggle + each banner still renders/dismisses).

### T1-D. Cohesion verdict — leave these (NOT safe to split now)
- `ConfigGenerator.cs` (1785), `CustomConfigInjector.cs` (1680): static builders with
  dense cross-method data dependency. Only safe move = extract constants. Otherwise
  keep cohesive.
- `NetworkPage.axaml` (2328): splitting into UserControls adds new `x:Class` +
  code-behind = NOT purely behavior-preserving. Out of scope for Track 1; if wanted,
  separate task using internal ControlTemplate reorg only.
- `AndroidApp.axaml.cs` (2384): already 19 partials, diminishing returns. Defer.

---

## 4. TRACK 2 — Deduplication (consolidate)

### T2-A. Cloudflare probe URL constant **[verified]**
`private const string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace"` — identical in 3 production files:
`Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs:27`, `Core/Services/VlessDeepVerifier.cs:39`,
`Android/AndroidFreeConfigDeepVerifier.cs:53`. Android also has `SecondaryProbeUrl =
"https://1.1.1.1/cdn-cgi/trace"` (69) — Android probe set is a **superset**, keep its
extra. Ignore non-code hits: `tools/android-e2e-test.sh:81`, `AndroidDeepVerifyBox.java:102`.
-> one shared const in Core (e.g. `DeepVerifyConstants.ProbeUrl`). **Risk: low.**

### T2-B. Deep-verify timeout constant **[verified]**
`static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(12)`, value identical:
`FreeConfigDeepVerifier.cs:33`, `VlessDeepVerifier.cs:41`, `AndroidFreeConfigDeepVerifier.cs:72`.
-> same shared `DeepVerifyConstants`. **Risk: low.** Combine with T2-A in one commit.

### T2-C. `FindFreePort` helper **[verified]**
Body BYTE-IDENTICAL in 3 verifiers; only accessibility differs:
`FreeConfigDeepVerifier.cs:471-478` (private), `VlessDeepVerifier.cs:377-384` (internal,
consumed by `VlessDeepVerifierTests.cs:238`), `AndroidFreeConfigDeepVerifier.cs:376-383`
(private). -> extract one Core helper (e.g. `NetPortUtil.FindFreePort()`), **keep
`internal`** so the existing test binds without widening public surface. Out of scope:
near-identical-but-different loopback probes at `SlipstreamManager.cs:642`,
`TgProxyManager.cs:299` (availability check, not ephemeral-port alloc). **Risk: low.**

### T2-D. Updater path constants vs AppPaths — NOT uniform, handle per updater **[verified]**
`AppPaths.cs` already owns `DataDir`(14), `ConfigDir/LogsDir/CacheDir/BinDir/...`(32-37),
`Wgturn{Dir,BinDir,CliExePath,VersionPath,VariantPath}`(54-60), `Slipstream*`(69-94).
It has NO zapret/ or tg-proxy/ members. It supports `OverrideDataDir` (Android sandbox).
- **WgturnUpdater.cs:93-98** re-declares `WgturnDir/BinDir/CliExePath/VersionFilePath/
  VariantFilePath` that `AppPaths:54-60` ALREADY defines -> **delete the redundant
  re-declaration**, point callers at `AppPaths.Wgturn*`.
- **ZapretUpdater.cs** rolls its own `_dataDir` (60-62) via
  `CommonApplicationData+"VPNRouter"`, paths at 80-84 -> **add zapret paths to
  AppPaths, then delegate.**
- **TgProxyUpdater.cs** same bespoke `_dataDir` (27-29), paths at 55-59 -> **add
  tg-proxy paths to AppPaths, then delegate.**
- **Correctness bonus:** Zapret/TgProxy bespoke `_dataDir` bypasses
  `AppPaths.OverrideDataDir` -> latent Android/test-override divergence; centralizing
  fixes it. **Treat the Zapret/TgProxy change as behavior-affecting** (it changes path
  resolution under override) -> needs MCP verify + careful test, not "pure dedup".
  **Risk: medium.** Do Wgturn (pure delete) first; Zapret/TgProxy separately.

**Do NOT consolidate** (intentional separation): App/Android localization wrappers;
per-platform deep verifiers (subprocess vs JNI).

---

## 5. TRACK 3 — Dead code (highest risk; codebase is clean — almost nothing) **[verified]**

**Precondition (rule #6):** before removing anything, run `dotnet build` across ALL
platform configs and harvest warnings. `TreatWarningsAsErrors`/`WarningsAsErrors` is
**NOT enabled anywhere** (Directory.Build.props records a substantial existing warning
backlog that would break the build), so the compiler does not currently surface unused members. The one
candidate below was found by grep, not the compiler — re-confirm zero callers at edit
time.

### T3-A. `TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent` — verified dead **[verified]**
`[Obsolete]` `public static void` at `Core/Services/TunAdapterDiagnostics.cs:225`,
body 225-282, doc-comment 200-222. **Zero production callers** (grep across Core/App/
Service/CLI/Android = none; replaced by `PreStartCleanupAsync`). NOT a pure delete —
4 test files reference it:
- **Remove method + doc:** delete `TunAdapterDiagnostics.cs` lines 200-282.
- **Edit tests:** delete 5 tests in `TunAdapterReadinessTests.cs` (`NonWindows_NoOp`
  ~30, `EmptyInterfaceName_NoOp` ~42, `NullInterfaceName_NoOp` ~51,
  `NonExistentAdapter_NoThrow` ~60, `StillCallable_ForBackcompat` ~427) + fix dangling
  doc cref line 17; delete 1 test (`EmitsNetshAdminEnabled` ~111) in
  `TunAdapterDiagnosticsProcessRunnerWireShapeTests.cs`; fix dangling see-cref line 16
  in `SingBoxManagerRestartTunHandshakeTests.cs` (avoid CS1574).
- **Keep:** negative-pin asserts at `TunAdapterReadinessTests.cs:562,591` and
  `SingBoxManagerRestartTunHandshakeTests.cs:93,198` (they pin that the method is gone
  / not invoked).
**Accept when:** build 0 errors (no CS1574), full test suite green, re-grep confirms
zero production callers at edit time. **Core-only / not UI-testable** — label as such.

### T3-B. `VpnEngine` `[Obsolete]` ctor — NOT dead, do not touch **[verified]**
`VpnEngine.cs:185` (body 191-215). Used by 1 production factory
(`PlatformServices.cs:87`, under `#pragma CS0618`) + 9 tests. The only other live
`[Obsolete]` attribute in the repo. **Leave it.**

### T3-C. `PlaceholderGuard` forwarder — NOT dead (13 live call sites)
Planned removal v3.1. Out of scope until call sites migrate to `PlaceholderDefense`.

---

## 6. Recommended order (safe -> risky)

1. **Baseline** — green build + regression on clean `main`; create work branch.
2. **T1-A** AppSettings -> file-per-type (one type per commit). Lowest risk.
3. **T1-B** SingBoxManager -> partials.
4. **T2-A + T2-B** probe URL + timeout constants (one commit).
5. **T2-C** FindFreePort -> Core helper.
6. **T1-C** MainWindowViewModel -> Banners + ThemeAndLogo partials (Windows hash gate + MCP verify).
7. **T2-D (Wgturn only)** delete redundant path re-declarations.
8. **Track 3 precondition** — full-platform build-warning pass; then **T3-A**.
9. **T2-D (Zapret/TgProxy)** — centralize paths (behavior-affecting; MCP verify). Last.

Each step = its own atomic commit through the blocking workflow. Re-open this file at
the start of each step and tick section 7 at the end.

---

## 7. Progress ledger (tick on completion, with commit SHA)

- [x] Baseline green: `dotnet build -c Release` 0 errors + regression 20/20 on clean main `ce05b2c9`. Working directly on `main` per project convention + the protocol's `push HEAD:main` (no separate branch); each item is its own atomic commit through the blocking gate. Remote `github`/Forgejo not configured this session — `origin` = GitHub is canonical. — SHA: ce05b2c9
- [x] T1-A AppSettings split DONE — AppSettings.cs 1390 -> 118 lines (root class only), 14 file-per-type extractions + 1 doc-cleanup, every diff a review-agent-confirmed pure move, build 0 err, full serialization/resolver/migrator suite 104/104. SHAs: fcc2051d EmergencyChannelSettings · 6820839c SubscriptionEntry · 6f32d176 CustomCategory · e8ce2f9b CustomRule · 6f36fd45 CustomDirectRule · 0d70036c SubscriptionEntry-doc-cleanup · 2d52eb54 CustomConfigEntry · c028b075 ProfileSource · 10a66cff VlessTransportConfigs · af4833c3 EngineSettings · e7a7c41b UserFreeSource · 8ba3ffde AppConfig · eb791b32 VlessConfig · a278b5ef VlessServerEntry · aa1d8d9f TunSettings
- [x] T1-B SingBoxManager partials DONE — SingBoxManager.cs 1713 -> 349 lines; 5 partial-class files (Health/LinuxStop/HotReload/CrashDetect/Lifecycle), anchor keeps all state + ctor + Dispose + OnAppDomainProcessExit. Every diff a review-agent-confirmed pure move; build 0 err. SingBox tests at 18 env-fail / 58 pass (= baseline; the 18 are ProgramData-write UnauthorizedAccess on this installed-VPNRouter dev box, green on clean CI — verified by stash/rebuild baseline compare). Source-characterization tests (ReadSourceFile + FindRepoFile families across ~9 files) made partial-aware: ReadAllParts (3 files) + shared SingBoxSourceText.ReadAll (6 sites) concatenate all SingBoxManager*.cs so an assertion finds its method in whichever partial holds it (assertions unchanged). SHAs: 796d74f5 Health · a567b6c4 LinuxStop · 5697cf5a HotReload · 973ab61f CrashDetect (+ReadAllParts) · 8783a51f Lifecycle (+SingBoxSourceText)
- [x] T2-A/B DeepVerify constants DONE (Core) — DeepVerifyConstants.cs (internal) centralizes ProbeUrl + OverallTimeout; FreeConfigDeepVerifier + VlessDeepVerifier alias it (local-alias, zero usage churn). Magic URL literal now in 1 Core file (was 2). build 0 err; 164/164 deep-verify+free-config tests. Android copy DEFERRED — AndroidFreeConfigDeepVerifier builds only under the separate .NET 10 Android toolchain (sln excludes it, my dotnet is .NET 8); fold Android in on a session that runs the local Android build. SHA: (this commit)
- [x] T2-C FindFreePort helper DONE (Core) — NetPortUtil.cs (internal) holds the byte-identical FindFreePort; FreeConfigDeepVerifier + VlessDeepVerifier call NetPortUtil.FindFreePort() (their private/internal copies deleted). VlessDeepVerifierTests rebound to NetPortUtil.FindFreePort(). Method now in 1 Core file (was 2). build 0 err; 39/39 deep-verify tests. Android copy DEFERRED (internal doesn't cross to the Android assembly + .NET 10 toolchain). SHA: (this commit)
- [~] T1-C ThemeAndLogo partial DONE; Banners DEFERRED — MainWindowViewModel.ThemeAndLogo.cs extracts the contiguous theme/logo block (_isDarkTheme/_themePreference/IsSystemThemePref/IsLightThemePref/IsDarkThemePref/_logoLight/_logoDark/LogoSource/LoadAsset/TryBuildInvertedLogo/_themeToggleText). MVM 7399 -> 7313. Characterization hash UNCHANGED (surface-neutral proof); build 0 err; HeadlessGuiTests 8/8 (MainWindow renders with moved bindings — UI verify for a pure move, in lieu of manual MCP which can't change for a surface-neutral move); review pure move + [ObservableProperty] attributes intact. Banners DEFERRED — its members (PlaceholderPruneNotice + ConflictingVpn warnings) are scattered + tangled with conflict-action orchestration (_lastConflicts/_skipVpnConflictThisSession/RefreshConflictingVpn/Ignore/Kill) heavily referenced from the connection flow (MVM 4053/4161/4171/6939); clean banner-only extraction needs careful per-member scattered work, lower value. SHA: (this commit)
- [x] T2-D Wgturn paths DONE — WgturnUpdater's DefaultWgturnDir + the 5 path properties (WgturnDir/BinDir/CliExePath/VersionFilePath/VariantFilePath) now forward to AppPaths.Wgturn* (single source) instead of re-declaring Path.Combine logic. Value-identity verified by reading both decl sets (byte-identical incl. CliExePath OS-conditional filename). Kept as thin forwarders (not deleted+repointed) because bare `BinDir` collides with AppPaths.BinDir (shared bin/) at repoint sites — forwarders avoid that ambiguity. build 0 err; MainWindowViewModelWgturnTests 2 env-fail/10 pass = baseline (stash-confirmed pre-existing ProgramData UnauthorizedAccess on this installed-VPNRouter dev box). SHA: (this commit)
- [x] Track-3 precondition DONE — re-grepped EnsureAdapterEnabledOrAbsent across Core/App/Service/CLI/Android/Tests at edit time: ZERO production callers (only the decl); dotnet build VPNRouter.sln -c Release 0 errors (warnings-as-errors off, so the compiler can't surface unused members — grep is the gate, as the plan notes). Android not locally build-harvestable (.NET 10 toolchain) but the grep covers its source.
- [x] T3-A DONE — removed the verified-dead [Obsolete] TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent (doc + 2 attributes + body, 89 lines). Deleted the 6 tests that CALLED it (5 in TunAdapterReadinessTests: NonWindows/EmptyInterfaceName/NullInterfaceName/NonExistentAdapter/StillCallable_ForBackcompat; 1 in TunAdapterDiagnosticsProcessRunnerWireShapeTests: EmitsNetshAdminEnabled incl. its CS0618 pragma pair). Fixed 2 dangling <see cref> -> <c> (CS1574 avoidance) in TunAdapterReadinessTests + SingBoxManagerRestartTunHandshakeTests. KEPT the negative-pin DoesNotContain asserts (they pin the method stays gone from LaunchProcess). build 0 err (no CS1574); re-grep zero callers; TunAdapter+RestartTunHandshake 45/45 pass. Core-only / not UI-testable. SHA: (this commit)
- [deferred — OUT OF SCOPE] T2-D Zapret/TgProxy path centralization — this /goal is STRICTLY behavior-preserving; the plan itself (T2-D, §4) flags this item as "behavior-affecting (it changes path resolution under OverrideDataDir) -> needs MCP verify + careful test, not pure dedup. Risk: medium." Delegating ZapretUpdater/TgProxyUpdater's bespoke CommonApplicationData _dataDir to AppPaths INTENTIONALLY changes override behaviour (the plan's "correctness bonus"), so it is NOT a pure member-move/const-extract and does not meet this /goal's DoD ("each diff only member-move/const-extract"). Deferred: needs the user's explicit go + a focused session with the App MCP available. Windows production behaviour would be identical (no override there), but tests asserting the bypass + the override path need careful handling.

---

## 8. Incidental findings (out of scope; file/track separately)

- **Stale docs:** `VPNRouter.App/CLAUDE.md` ("Partial classes" section) says
  MainWindowViewModel has **10** partials; actual = **12** (LocalizedLabels, ConnStats
  added). Doc-only fix; mirror to `.agents`/`AGENTS.md` per the mirror rule.
- **Latent correctness:** Zapret/TgProxy `_dataDir` bypasses `AppPaths.OverrideDataDir`
  (see T2-D) — not just duplication, an Android/test override divergence.

---

## 9. Verification provenance

- Inventory: 3 read-only Explore agents (size map, dead code, dedup, split seams), 2026-06-25.
- Re-verification: workflow `verify-refactor-inventory`, run `wf_af1156ee-fb4`, 5 agents,
  2026-06-25. Corrections folded in and marked **[verified]** above.
- Re-run verification before acting if this file is older than the current `main` HEAD
  by more than a few commits — call sites and line numbers drift.
