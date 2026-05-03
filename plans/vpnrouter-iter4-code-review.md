# Iter#4 — Comprehensive code review of all modules

**Date**: 2026-05-04
**Trigger**: user request «сделай комплексный пошаговый ревью кода каждого модуля». Spawned 2 parallel review agents (one for Core, one for App) and self-reviewed CLI/Service/Tests/Android.

**Scope**: every project in the solution (Core, App, CLI, Service, Tests, Android, GUI bootstrap, Tools/PoolAggregator).

**Output**: this document = all findings. r8 ships only the **P0/P1 fixes that don't risk scope creep**. Larger refactors (MainWindowViewModel split, IDisposable lifecycle, dead-code purge, A11y rollout, i18n inline-ternary cleanup) are documented as backlog so future iterations can pick them up.

## Summary table

| Module | LOC | Files | P0/P1 | P2 | Notes |
|---|---|---|---|---|---|
| Core | ~20,321 | 80 | **5** | 9 | HealthMonitor concurrency races, VpnEngine warmup NRE |
| App | ~19,132 | 47 | **5** | 12 | IDisposable leak, hardcoded hex, SmpAutostart bug |
| CLI | 1,076 | 10 | 0 | 0 | Clean |
| Service | 885 | 3 | 0 | 1 | StopAsync swallows component-stop errors silently |
| Tests | 6,106 | 10 | 0 | 1 | UnitTest1.cs is a 5,016-LOC god-class (split candidate) |
| Android | (Phase 0/1.D) | 6 | 0 | 0 | Phase 1.E next: replace SmokeTestConfig with real ConfigGenerator |

## Module-by-module findings

### 1. VPNRouter.Core (P0/P1: 5)

**P0/P1 — fixed in r8**:

- **HealthMonitor.cs:174-310 — concurrent AttemptRestart races** (FIXED).
  `AttemptRestart` invokable from `OnHealthTick` (periodic timer thread) +
  `OnSingBoxCrashed` (Process.Exited threadpool thread). Pre-r8:
  non-atomic `_restartAttempts++`, non-atomic CTS swap. Two concurrent
  callers could pass the cap gate together OR leak/double-dispose
  the CTS. **r8 adds `_attemptRestartLock` serialising the increment +
  CTS swap**. Counter resets in the Task.Delay continuation are also
  now under the lock.
- **VpnEngine.cs:581-604 — fire-and-forget warmup NRE on `_singBox.Pid`** (FIXED).
  Lambda captures `_singBox` by-reference; quick Connect→Disconnect can
  null `_singBox` between the outer assignment and lambda body.
  **r8 captures `_singBox.Pid` into a `pidSnapshot` local before
  `Task.Run`**.

**P0/P1 — deferred (lower risk in current state)**:

- **HealthMonitor.cs:174-219 — OnHealthTick re-entry guard**.
  `System.Threading.Timer` can call back re-entrantly if a previous
  callback is still running. The body does cross-process WMI/HttpClient
  calls; on strict-mode 5 s ticks this is non-trivial. **Risk**: two
  ticks can each call AttemptRestart → with the new lock the second
  is now serialised, so the visible damage is bounded. Worth adding
  `Interlocked.CompareExchange` gate but not P0 once the AttemptRestart
  lock is in place.
- **HealthMonitor.cs:226-241 — OnSingBoxCrashed → firewall.EnableBlockRules
  vs VpnEngine.Stop teardown**. `_firewall` can be null/disposed by
  the time the crash event fires during graceful shutdown. The `_isStopping`
  guard at line 228 catches the most common case but not the window
  between "Stop is starting" and "_isStopping = true is published".
  **Risk**: `NullReferenceException` logged, swallowed by the existing
  try/catch. Low impact.
- **VpnEngine.cs:408-409 — `scanTask.Wait + .Result` swallows inner
  exception**. Use `await scanTask.WaitAsync(...)`. P3 cosmetic.

**P2 — code quality (deferred)**:

- VpnEngine.cs:212-260 vs 692-780 — ~80 LOC profile-merge logic
  duplicated between `StartAsync` and `ApplyAsync`. Extract
  `BuildActiveProfile(settings, collection)` helper. (Silent-leak class
  of bug if branches drift.)
- ConfigGenerator.cs — three near-identical type→route switch blocks;
  worth a `ApplyRuleTypeToRoute` helper.
- Wildcard filter `!Contains('*') && !Contains('?')` repeated in 4+
  places; promote to `ProcessScanner.IsLiteralProcessName()`.
- SingBoxManager.cs:512-587 — 3 PSI blocks differing only in
  FileName/Arguments; refactor to one `BuildPsi(file, args)`.
- ConfigGenerator.cs:117-119 — hardcoded GitHub URL
  `AdBlockRuleSetUrl`; move to `Constants.cs`.
- SingBoxManager.cs:31 — static `HttpClient` global Timeout=3s; not
  cancellable from `Stop()`. Add explicit CTS like `TryHotReload` does.

**Surface metrics**:

- 80 .cs files, ~20,321 LOC.
- Largest 3: `VpnEngine.cs` (1,233), `UpdateChecker.cs` (1,225),
  `CustomConfigInjector.cs` (1,188).
- TODO/FIXME/HACK count: 2 (both acceptable known-issues).
- Public types with no XMLdoc: small DTOs only.

### 2. VPNRouter.App (P0/P1: 5)

**P0/P1 — fixed in r8**:

- **MainWindowViewModel.cs:5520..5605 — hardcoded hex in
  ShowFreeConfigSecurityWarningAsync** (FIXED).
  6 `Avalonia.Media.Brush.Parse("#…")` literals violated Rule B3
  (no raw hex) AND broke dark-theme rendering (hex is theme-blind).
  **r8 replaces with `TryFindResource` against semantic tokens
  (`SuccessSolidBrush`, `WarningBgBrush`, etc.) with sensible
  fallbacks for headless/test mode**.
- **MainWindowViewModel.SimpleMode.cs:80..113 — SmpAutostartChecked
  writes `_settings.App.AutostartVpn` directly** (FIXED).
  Bypassed the `[ObservableProperty] AutostartVpn` setter, so the
  Advanced-mode checkbox showed stale state until next
  LoadSettingsIntoUI. **r8 routes through the property setter** so
  PropertyChanged fires for the Advanced checkbox binding.
- **MainWindowViewModel.cs:18 + 21 — duplicate `using
  VPNRouter.Core.Platform;`** (FIXED — collapsed to one).

**P0/P1 — deferred (bigger refactor scope)**:

- **MainWindowViewModel.cs:5614 — no IDisposable**.
  `_runtimeStatusTimer` (DispatcherTimer) and `_subRefreshTimer` only
  stop on the explicit `Quit()` / `OnEngineStatus("Stopped")` path —
  leak on unhandled exit / future ReloadMainWindow path.
  `FreeConfigsVm.Dispose()` never called either. **Backlog**: implement
  `IDisposable` on the VM, hook from `MainWindow.Closed`.
- **MainWindowViewModel.cs:5417..5503 — ApplyFreeConfigAsync doesn't
  set IsConnected on success**.
  Relies on `OnEngineStatus` event arriving. Race: macOS 8 s grace
  window in `SyncConnectedWithVpnRuntime` bypassed. **Backlog**:
  mirror `ToggleConnectionAsync` post-success state writes.
- **MainWindowViewModel.cs:2010..2078 — ResetConfigCommand disarm
  task leaks CTS**.
  Doesn't pass `TaskScheduler.Default` for the continuation; CTS
  not disposed after the 5-second window. **Backlog**: refactor to
  using-semantics.

**P2 — code quality (backlog)**:

- MainWindowViewModel.cs is **5614 LOC**. Split out Zapret command
  surface (~10 commands, 3779-4189) and Subscription/SubRefresh block
  (3514-3777) into their own partials (same pattern as
  SimpleMode/RuntimeStatus already use).
- MainWindowViewModel.Localization.cs has **489 L_* properties**.
  `RefreshL10nProxies` reflects on every `Public|Instance` property
  on toggle — fine perf but fragile. Cache in `static readonly string[]`.
- Two parallel localization surfaces: `Lbl*` (legacy) + `L_*`
  (generated). 489 L_ + ~80 Lbl_ — many 1:1 duplicates. Decide on
  one and migrate XAML.
- MainWindowViewModel.cs has **21 `catch { }` blocks** — many pragmatic,
  several swallow user-visible failures (`KillAllZapret`, `OpenLeakTest`,
  `OpenFolderInExplorer`, `OpenUrl`, `CopyToClipboard`). Add at least
  `_logger.Debug(...)`.
- MainWindowViewModel.cs:516..535 — `_typeValidatorMap` regex
  `^([A-Z]:\\|/).+$` accepts `c:\foo` on Linux. Gate by `OperatingSystem.IsWindows()`.
- NetworkPage.axaml is **2172 LOC** — largest XAML. `IosToggleCheckBox`
  ControlTheme + 5 Style rules at file scope are reusable; consider
  extracting to `Styles/Tokens.axaml` (or a new `Styles/RulesStyles.axaml`).

**Dead code (backlog — separate cleanup commit)**:

- `Converters.cs:228..251` — `AppsTabVisibleConverter` and
  `EmptyCustomConverter` (two `IMultiValueConverter` singletons),
  no XAML reference.
- `MainWindowViewModel.cs:1844..1848` — `L_TgProxySetupCta`,
  `L_TgProxySetupSubtitle`, `L_TgProxySetupStep`,
  `L_TgProxyClientAutoHint`, `L_TgProxyAdvanced`. Added in v2.31.6-r1
  setup-cascade, dropped in r3. Keep-for-backward-compat comment exists
  but no XAML binds them. Drop the 5 getters + the 5 underlying
  `Strings.TgProxySetup*` keys.
- `MainWindowViewModel.cs:5213..5264` — `ReloadMainWindowForLocalization`,
  comment explicitly says "no longer wired into the toggle path". Delete.
- `MainWindowViewModel.cs:657..666` — `CustomDirectRulesText` /
  `CustomDirectRulesErrorText` aliases, "v2.29.0-r4 legacy" — no XAML
  reference. Drop.
- `Localization/Strings.cs:1074..1077` —
  `CustomDirectRulesTitle/Description/Placeholder/ErrorHeader` aliases
  + matching `L_*` getters at MainWindowViewModel.Localization.cs:125-128.
  Drop.
- `MainWindowViewModel.Localization.cs:312` — `L_LblRoutingMode` not
  referenced from any XAML. Drop.
- `MainWindowViewModel.cs:222..224` — `OnConfigModeIndexChanged`
  no-op; "ComboBox removed in v2.5.0".
- `MainWindowViewModel.cs:1736` — `LblTabManual` duplicate of
  `LblTabServers`.

**A11y / i18n issues (backlog — dedicated iteration)**:

| Page | Interactive | AutomationProperties.Name |
|---|---|---|
| NetworkPage.axaml | 91 | 15 |
| ServersPage.axaml | 16 | 0 |
| SubscribePage.axaml | 11 | 1 |
| FreeConfigsPage.axaml | 8 | 1 |
| TelegramPage.axaml | 9 | 0 |
| SimplePage.axaml | 7 | 0 |

Plus: `FormatBadgeText` returns 🟢 🔴 ⚪ emoji as badge text. Narrator
doesn't reliably announce these AND violates project no-emoji rule.
Multiple `IsRussian ?` ternaries inline (Rule D1 violation) — should
live in `Strings.cs`.

**Surface metrics**:

- 47 files (.cs/.axaml), ~19,132 LOC.
- Largest: `MainWindowViewModel.cs` (5,614), `FreeConfigsPageViewModel.cs`
  (1,871), `Strings.cs` (1,385).

### 3. VPNRouter.CLI

Clean. `Program.cs` is a tidy Spectre.Console root with explicit logging
setup + CommandApp wiring; `StartCommand.cs` (305 LOC) handles config
load → admin check → SubscriptionResolver → VpnEngine.StartAsync with
clear error paths and dry-run support; helpers (`StateFile`, `AdminHelper`,
`ProfileSourceFactory`) are small and focused.

No P0/P1 findings. No P2 findings worth a code change.

### 4. VPNRouter.Service

Well-structured 466 LOC `VPNRouterService.cs` (BackgroundService) with
clear lifecycle separation: ExecuteAsync orchestrates, three Autostart*
methods handle their concerns, OnConfigChanged for hot-reload, StopAsync
for teardown.

**P2 — code quality (defensible-as-is)**:

- StopAsync (lines 439-444) has 6× `catch { }` empty-swallow blocks
  during component shutdown. Defensible (we want all components stopped
  even if one throws), but lacks logging of what got swallowed. Could
  be `catch (Exception ex) { _logger.LogWarning(ex, "[Service] {component} stop failed (non-fatal)"); }`.

### 5. VPNRouter.Tests

10 .cs files, 6,106 LOC.

**P2 — UnitTest1.cs is a 5,016-LOC god-class** holding 25 test classes
of vastly different topics (subscription parsers, ConfigGenerator,
LeakProtection, FreeConfigCache migration, BoolToChevronConverter,
HealthMonitorTimerRace, etc.). Splitting per topic into separate files
(`SubscriptionFetcherParserTests.cs`, `ConfigGeneratorTests.cs`, etc.)
would be high-value housekeeping but the migration is risky for test
discovery + naming. **Backlog**: do per-version-cycle (extract the
classes you're already touching anyway).

No P0/P1.

### 6. VPNRouter.Android (Phase 0 / 1.D state)

Phase 1.D current. Activity wired, Connect/Disconnect buttons working,
Java VpnRouterService running libbox.aar with smoke-test config (direct
outbound, no proxy). End-to-end runtime verified on KYOCERA A101BM
(Android 12) per memory log.

**Phase 1.E (next concrete step)**: replace `MainActivity.SmokeTestConfig`
with a real generated config from `VPNRouter.Core.ConfigGenerator`.
Blocker logged: `<ProjectReference>` `<Properties>EnableAndroidTarget=true</Properties>`
doesn't always propagate. Reverted in earlier session; needs different
multi-target approach.

No P0/P1 findings (it's a scaffold / phase-locked WIP, not in production).

### 7. VPNRouter.GUI (Go bootstrap stub)

Self-contained 242-line Go binary handling the post-update bootstrap
when pre-r6 ApplyUpdateWindows can't write locked .NET DLLs in-process.
Well-commented, focused, no findings.

### 8. VPNRouter.Tools/PoolAggregator

Single-file aggregator for free-configs server-side pool.json. Small.
Not reviewed deeply (out of MVP-quality concern).

## What r8 ships

| Module | Change | File:line |
|---|---|---|
| Core | HealthMonitor concurrency lock | `HealthMonitor.cs:55-72, 243-310` |
| Core | VpnEngine warmup PID snapshot | `VpnEngine.cs:578-604` |
| App | Hardcoded hex → tokens in security warning dialog | `MainWindowViewModel.cs:5512-5605` |
| App | SmpAutostartChecked → property setter | `MainWindowViewModel.SimpleMode.cs:80-113` |
| App | Duplicate `using` removed | `MainWindowViewModel.cs:18-21` |

**NOT** in r8 (backlogged for future iterations):

- Big refactors (MainWindowViewModel split, IDisposable lifecycle).
- Dead-code purge (separate cleanup commit).
- A11y rollout across pages.
- i18n inline-ternary → Strings.cs migration.
- HealthMonitor OnHealthTick re-entry guard (lower-risk after AttemptRestart lock).
- `scanTask.Wait + .Result` → `WaitAsync` swap.
- Test class split (UnitTest1.cs god-class).
- Android Phase 1.E (executed separately, not in r8).

## Verification

- `dotnet build -c Release` → 0 errors, 44 warnings (all pre-existing).
- Regression suite (20 tests) + HealthMonitorRecoveryGapTests (5) →
  **25/25 passed** in 111 ms.

## Process notes

- 2 review agents (Core, App) ran in parallel and returned
  comprehensive structured reports. Total compute ~5 min wall-clock,
  ~370k tokens combined. Findings list above is cherry-picked for
  actionability — the agents' raw reports are richer (full P3 lists,
  metrics, file-by-file LOC). Future iterations can mine the backlog.
- CLI/Service/Tests reviewed myself (smaller surface, faster).
- Android reviewed via direct file reads (Phase 1.D state is small +
  well-documented).

## Backlog priority (for future iterations)

1. **VPNRouter.App MainWindowViewModel IDisposable lifecycle** — real
   leak risk on edge-case exits, big refactor.
2. **VPNRouter.App dead-code purge** — single cleanup commit removing
   ~10 unused L_* / Strings keys / VM helpers documented above.
3. **VPNRouter.App A11y rollout** — add AutomationProperties.Name to
   ServersPage / TelegramPage / SimplePage interactive elements
   (largest gaps).
4. **VPNRouter.App i18n inline-ternary cleanup** — move `IsRussian ?`
   ternaries from MainWindowViewModel.cs into Strings.cs.
5. **VPNRouter.Core profile-merge dedup** — extract
   `BuildActiveProfile` helper from VpnEngine StartAsync/ApplyAsync.
6. **VPNRouter.Core HealthMonitor OnHealthTick re-entry guard**.
7. **VPNRouter.Tests UnitTest1.cs split** — per-topic, do incrementally.
8. **VPNRouter.Service StopAsync catch{} → logged warning**.
