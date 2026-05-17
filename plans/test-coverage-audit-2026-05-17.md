# Test Coverage Audit (v2.32.3 baseline)

Date: 2026-05-17. Read-only inventory ahead of v3.0 refactor. Numbers from
`grep -c "\[Fact\]|\[Theory\]|\[AvaloniaFact\]|\[AvaloniaTheory\]"` and
`grep -l <Service>` against `VPNRouter.Tests/*.cs`.

## Summary

- **Total test methods**: 765 across 56 `.cs` files (19,422 LOC).
- **Test classes**: ~85 (42 inside `UnitTest1.cs` alone, ~43 in dedicated files).
- **Core services**: 60 files (23,430 LOC). **42 / 60 (70 %) have at least one
  test file reference**, only **~22 have a dedicated, named test class**.
- **Critical-path coverage**: yes — leak / placeholder / sanity layers heavily
  pinned. F-A..F-E placeholder defense = **65 tests** across 8 files.
- **Top 5 blockers for v3.0 refactor confidence** (none have dedicated tests):
  1. `VpnEngine.cs` (1,658 LOC) — orchestrator; only 8 fragmentary touches.
  2. `SingBoxManager.cs` (989 LOC) — process lifecycle / Stop race / Clash API hot-reload.
  3. `UpdateChecker.cs` (1,387 LOC) — only mirrored by CI workflow `test-windows-update.yml`, no unit tests.
  4. `HostsManager.cs` / `WindowsDnsHardening.cs` / `DnsFlusher.cs` — zero tests, all touch live OS state.
  5. `MainWindowViewModel.cs` (6,753 LOC across 7 partial files) — only 3 dedicated VM test files.

## §1 Test class inventory

`VPNRouter.Tests/CLAUDE.md` lists ~24 curated classes; actual count is ~85.
**Stale** — 42 classes inside `UnitTest1.cs` (incl. `LeakProtectionMultiProtocolTests`,
`CustomRulesV2_30_*`, `MergeUserCustomizationTests`, `PowerEventListenerTests`,
`EmergencyChannel*Tests`, `PlaceholderGuardTests`) are **not in the curated
table**. Newly-added dedicated files since CLAUDE.md was written (28 files):

| File | Tests | Service touched |
|---|---|---|
| `AndroidCategoryLocalizationTests.cs` | 4 | BuiltInAndroidProfiles |
| `AndroidDpiBypassInjectorTests.cs` | 13 | AndroidDpiBypassInjector |
| `AndroidStorageSaneTests.cs` | 12 | AndroidStorageSane |
| `AppAutostartTgProxyTests.cs` | 6 | TgProxyManager autostart |
| `AutoFailoverEngineTests.cs` | 11 | AutoFailoverEngine (F-E) |
| `AutostartContractTests.cs` | 8 | autostart cross-platform |
| `BypassRussianTrafficAbTest.cs` | 1 | ConfigGenerator geo branch |
| `CacheRecoveryTests.cs` | 21 | CacheRecovery |
| `ConfigGeneratorExcludeModeTests.cs` | 9 | ConfigGenerator (AM-1) |
| `ConfigGeneratorIncludeModeTests.cs` | 4 | ConfigGenerator (AM-2) |
| `ConfigGeneratorRemoteRuleSetGuardTests.cs` | 2 | ConfigGenerator |
| `ConfigSanityCheckTests.cs` | 14 | ConfigSanityCheck (F-D) |
| `ConfigShareDocumentTests.cs` | 18 | ConfigShareDocument |
| `ConflictingVpnDetectorTests.cs` | 6 | ConflictingVpnDetector |
| `CrashReporterScrubberTests.cs` | 8 | CrashReporter |
| `CustomConfigPlaceholderTests.cs` | 7 | CustomConfigInjector (Phase 2c) |
| `HealthMonitorLeakValidationTests.cs` | 1 | HealthMonitor |
| `HelperCmdParserGuardTests.cs` | 6 | helper.cmd (CMD parser bug) |
| `LaunchFailureCounterTests.cs` | 20 | LaunchFailureCounter |
| `LeakProtectionScopeAwareTests.cs` | 9 | LeakProtection (F-D) |
| `MainWindowViewModelAppsModeTests.cs` | 13 | MainWindowViewModel apps |
| `MainWindowViewModelWgturnTests.cs` | 12 | MainWindowViewModel wgturn |
| `OrphanCleanupGuardTests.cs` | 1 | OrphanCleanup |
| `PerAppFilterModeTests.cs` | 4 | per-app filter mode |
| `PlaceholderInputGateTests.cs` | 6 | PlaceholderGuard (Phase 2a) |
| `ProfileApplicationTests.cs` | 8 | ProfileApplication |
| `RuleSetCacheManagerTests.cs` | 12 | RuleSetCacheManager |
| `ServiceAppCoexistenceTests.cs` | 6 | service/app coexistence |
| `SettingsLoaderRobustnessTests.cs` | 16 | SettingsLoader |
| `SettingsMigratorAppsModeTests.cs` | 8 | SettingsMigrator (AM) |
| `SettingsMigratorLegacyVlessServersCleanupTests.cs` | 8 | SettingsMigrator |
| `SettingsMigratorPlaceholderTests.cs` | 7 | SettingsMigrator (F-B) |
| `SettingsMigratorWgturnPathMigrationTests.cs` | 7 | SettingsMigrator (wgturn) |
| `SettingsValidatorTests.cs` | 19 | SettingsValidator |
| `SingleInstanceGuardTests.cs` | 2 | LockFile (indirect) |
| `StorageBlobRecoveryTests.cs` | 8 | StorageBlobRecovery |
| `SubscriptionFetcherPlaceholderTests.cs` | 6 | SubscriptionFetcher (F-A2) |
| `TcpTlsProbeRealityDispatchTests.cs` | 2 | TcpTlsProbe |
| `TgProxyAutostartLoggingTests.cs` | 11 | TgProxyManager |
| `TunAdapterReadinessTests.cs` | 13 | TunAdapterDiagnostics |
| `UpdateBackupTests.cs` | 9 | UpdateBackup |
| `VlessServersResolverScopeGuardTests.cs` | 9 | VlessServersResolver (F-A) |
| `VpnEngineApplyEscalationTests.cs` | 4 | VpnEngine |
| `VpnEngineRemoveExcludedAppsTests.cs` | 8 | VpnEngine |
| `VpnEngineTunFingerprintTests.cs` | 12 | VpnEngine |
| `WgturnUpdaterTests.cs` | 17 | WgturnUpdater |

**Recommendation**: bump `VPNRouter.Tests/CLAUDE.md` table to include all 56
files (manageable since most fit one row).

## §2 Per-service coverage matrix

Heuristic: `grep -l <ServiceName> VPNRouter.Tests/*.cs`. "Files" = test files
mentioning the service; "Dedicated" = service has its own named test class.

| Service | LOC | Test files | Dedicated class? | Status |
|---|---|---|---|---|
| ConfigGenerator | 1,353 | 9 | Yes (`ConfigGeneratorTests` +6) | **WELL-TESTED** |
| VpnEngine | 1,658 | 8 | Partial (3 narrow tests) | PARTIAL |
| SettingsLoader | 641 | 7 | Yes (`SettingsLoaderRobustnessTests`) | **WELL-TESTED** |
| ConfigSanityCheck | 353 | 7 | Yes (14 tests, F-D) | **WELL-TESTED** |
| SettingsMigrator | 544 | 6 | Yes (4 dedicated files, 30 tests) | **WELL-TESTED** |
| HealthMonitor | 593 | 5 | Yes (3 dedicated classes, 7 tests) | PARTIAL |
| ConfigGenerator | 1,353 | 9 | Yes | WELL-TESTED |
| LeakProtection | 623 | 5 | Yes (28 tests, multi-proto + scope) | **WELL-TESTED** |
| PlaceholderGuard | 184 | 5 | Yes (multi-file gate) | **WELL-TESTED** |
| VlessServersResolver | 251 | 5 | Yes (scope + base) | **WELL-TESTED** |
| SingBoxManager | 989 | 4 | No (referenced only) | PARTIAL |
| OrphanCleanup | 139 | 4 | Yes (1 test) | PARTIAL |
| TgProxyUpdater | 321 | 3 | No | PARTIAL |
| RuleSetCacheManager | 219 | 3 | Yes (12 tests) | **WELL-TESTED** |
| SubscriptionResolver | 98 | 2 | No | PARTIAL |
| GeoDataDownloader | 184 | 2 | No | PARTIAL |
| HealthCheck | 405 | 2 | No | PARTIAL |
| UpdateChecker | 1,387 | 2 | No — only mirrored by CI workflow | PARTIAL |
| ResilientStarter | 168 | 2 | No | PARTIAL |
| TgProxyManager | 434 | 2 | No (only autostart side) | PARTIAL |
| CacheRecovery | 297 | 2 | Yes (21 tests) | **WELL-TESTED** |
| TcpTlsProbe | 630 | 2 | Yes (Reality dispatch, 2 tests) | PARTIAL |
| VlessUriParser | 165 | 2 | Yes (UnitTest1) | **WELL-TESTED** |
| ServerUriParser | 356 | 2 | Yes (UnitTest1) | **WELL-TESTED** |
| CustomConfigInjector | 1,254 | 2 | Yes (UnitTest1 + Placeholder) | **WELL-TESTED** |
| SubscriptionFetcher | 298 | 2 | Yes (`SubscriptionFetcherParserTests` + Placeholder) | **WELL-TESTED** |
| ProcessScanner | 255 | 1 | No (UnitTest1 only) | PARTIAL |
| TunOwnershipLock | 169 | 1 | No | PARTIAL |
| ZapretUpdater | 724 | 1 | No | PARTIAL |
| CustomDirectRulesParser | 195 | 1 | Yes (UnitTest1) | **WELL-TESTED** |
| CustomRulesParser | 314 | 1 | Yes (3 UnitTest1 classes) | **WELL-TESTED** |
| CustomRulesImportExport | 476 | 1 | Yes (UnitTest1) | **WELL-TESTED** |
| RuntimeStatusDetector | 98 | 1 | Yes (handle-leak) | PARTIAL |
| PowerEventListener | 162 | 1 | Yes (UnitTest1) | PARTIAL |
| FirewallManager | 409 | 1 | Yes (localized netsh) | PARTIAL |
| UpdateBackup | 341 | 1 | Yes (9 tests) | **WELL-TESTED** |
| SettingsValidator | 304 | 1 | Yes (19 tests) | **WELL-TESTED** |
| LaunchFailureCounter | 240 | 1 | Yes (20 tests) | **WELL-TESTED** |
| ProfileManager | 429 | 1 | Yes (JSON DoS guard, 2 tests) | PARTIAL |
| StorageBlobRecovery | 96 | 1 | Yes (8 tests) | **WELL-TESTED** |
| AndroidDpiBypassInjector | 161 | 1 | Yes (13 tests) | **WELL-TESTED** |
| ConfigShareDocument | 315 | 1 | Yes (18 tests) | **WELL-TESTED** |
| AndroidStorageSane | 118 | 1 | Yes (12 tests) | **WELL-TESTED** |
| BuiltInAndroidProfiles | 154 | 1 | Yes (localization, 4 tests) | PARTIAL |
| ProfileApplication | 81 | 1 | Yes (8 tests) | **WELL-TESTED** |
| CrashReporter | 162 | 1 | Yes (scrubber, 8 tests) | PARTIAL |
| ConflictingVpnDetector | 148 | 1 | Yes (6 tests) | **WELL-TESTED** |
| TunAdapterDiagnostics | 484 | 1 | Yes (readiness, 13 tests) | **WELL-TESTED** |
| ZapretManager | 267 | 1 | No (only updater side) | PARTIAL |
| AutoFailoverEngine | 356 | 1 | Yes (11 tests, F-E) | **WELL-TESTED** |
| WgturnUpdater | 511 | 1 | Yes (17 tests) | **WELL-TESTED** |
| SafeMode | 21 | 2 | No (only mention) | PARTIAL |
| **DnsFlusher** | 114 | **0** | — | **UNTESTED** |
| **HostsManager** | 256 | **0** | — | **UNTESTED** |
| **LockFile** | 110 | **0** | — | **UNTESTED** |
| **NetworkInterfaceDetector** | 171 | **0** | — | **UNTESTED** |
| **WindowsDnsHardening** | 249 | **0** | — | **UNTESTED** |
| **ZapretActions** | 562 | **0** | — | **UNTESTED** |
| **EtwProcessMonitor** | 184 | **0** | — | **UNTESTED** |
| **VlessDeepVerifier** | 606 | **0** | — | **UNTESTED** |
| **QrCode** | 599 | **0** (only file-name mention in `ConfigShareDocumentTests`) | — | **UNTESTED** |

Tally: **9 fully untested services (1,851 LOC)**, **~30 PARTIAL**, **~21 WELL-TESTED**.

## §3 Critical untested paths

Ranked by blast radius:

| Service | LOC | Priority | Why |
|---|---|---|---|
| `WindowsDnsHardening` | 249 | **CRITICAL** | Writes `netsh dnsclient` policy → controls user's system DNS. Failure = DNS leak. Mirror of `FirewallManager` which IS tested. |
| `HostsManager` | 256 | **CRITICAL** | Writes `%SystemRoot%\System32\drivers\etc\hosts` (Discord voice fix). Wrong entry = total resolution break. |
| `EtwProcessMonitor` | 184 | **HIGH** | Real-time process scanner, drives debounce in `HealthMonitor`. Stale event = wrong routing. |
| `VlessDeepVerifier` | 606 | **HIGH** | Deep server probe (handshake). FreeConfigs verdict relies on this; false-positive = bad server marked good. |
| `LockFile` / `TunOwnershipLock` | 110 / 169 | **HIGH** | Single-instance + TUN race. v2.31.x recovery work touches this; regression = double-VPN double-init. |
| `ZapretActions` | 562 | **HIGH** | Builds netsh / strategy .bat (Cygwin gotcha — see CLAUDE.md). Largest untested file. |
| `DnsFlusher` | 114 | **MED** | `ipconfig /flushdns` wrapper. Failure mode is silent stale cache, not leak. |
| `NetworkInterfaceDetector` | 171 | **MED** | Adapter enumeration; consumed by leak detection. |
| `QrCode` | 599 | **LOW** | Read-only UI helper; rendering bug = ugly QR, not security. |

`SingBoxManager` (989 LOC) is technically PARTIAL but the **Stop / Restart / Clash hot-reload paths** that lesson-rich CLAUDE.md notes call out are
not directly unit-tested. Treat as **CRITICAL** for v3.0 (state-machine candidate).

## §4 UnitTest1.cs extraction plan

`UnitTest1.cs` = 6,169 LOC / 42 classes / 313 tests. Build cost: every test
run recompiles this monster. Suggested extraction (preserve names verbatim,
one class per file):

| Extract | Lines | Target file |
|---|---|---|
| `LeakProtectionAppSettingsTests` | 461-661 | `LeakProtectionAppSettingsTests.cs` |
| `LeakProtectionTests` | 662-1159 | `LeakProtectionTests.cs` |
| `LeakProtectionMultiProtocolTests` | 4478-4645 | `LeakProtectionMultiProtocolTests.cs` |
| `VlessUriParserTests` | 1160-1328 | `VlessUriParserTests.cs` |
| `ServerUriParserTests` | 4333-4477 | `ServerUriParserTests.cs` |
| `CustomConfigInjectorTests` | 1329-1916 | `CustomConfigInjectorTests.cs` |
| `FreeConfig*Tests` (×7 classes) | 2371-3201 + 5178-5292 | one file each → `FreeConfigAggregatorPreserveTests.cs` etc. |
| `CustomRulesV2_30_*Tests` (×3) | 3739-4157 | `CustomRulesV2_30_*Tests.cs` per class |
| `CustomDirectRules*Tests` (×2) | 3257-3625 | `CustomDirectRules*Tests.cs` per class |
| `EmergencyChannel*Tests` (×4) | 5677-6002 | `EmergencyChannelTests.cs` (one file, four classes) |
| `MergeUserCustomizationTests` | 5471-5615 | `MergeUserCustomizationTests.cs` |
| `PowerEventListenerTests` | 5616-5676 | `PowerEventListenerTests.cs` |
| `PlaceholderGuardTests` | 6003-end | `PlaceholderGuardTests.cs` |
| Other top-level `ConfigGeneratorTests`, `GetEffectiveServersTests`, etc. | 11-460, 1917-2370 | one file each |

Estimated effort: **mechanical** — copy class, fix `using`s, delete from
`UnitTest1.cs`. Should be incremental (don't rip in one PR) to avoid merge
hell. Goal: `UnitTest1.cs` becomes 0 LOC and gets deleted.

## §5 Integration test gaps

- **sing-box check integration**: 3 tests (CLAUDE.md says 2 — undercount).
  Names: `Inject_ActualCustomConfig_SingBoxCheck`,
  `Inject_WithBypassRussianTraffic_PassesSingBoxCheck`,
  `Generate_FromSubscribeMode_PassesSingBoxCheck`. All graceful-skip if
  `sing-box.exe` missing → CI without binary returns "PASS" without
  exercising the assertion (silent gap).
- **F-A..F-E placeholder layers**: 65 tests across 8 files:
  `PlaceholderInputGateTests` (6), `SubscriptionFetcherPlaceholderTests` (6),
  `CustomConfigPlaceholderTests` (7), `SettingsMigratorPlaceholderTests` (7),
  `AutoFailoverEngineTests` (11), `ConfigSanityCheckTests` (14),
  `LeakProtectionScopeAwareTests` (9), `VlessServersResolverScopeGuardTests` (9).
  Excellent — this is the most thoroughly-tested feature in the repo.
- **Auto-update integration**: CI-only via `test-windows-update.yml`. No
  unit-test mirror of `UpdateChecker` (1,387 LOC). v2.31.7 helper.cmd bug
  slipped through precisely because no unit test exercised the CMD parser
  expansion logic — `HelperCmdParserGuardTests.cs` (6 tests) now closes
  exactly that gap. Add similar guards for `InstallHealthCheck` reflection,
  `SelfRepair` channel-aware `-Prerelease` logic.
- **Migration tests**: 4 dedicated files (`SettingsMigrator*`) covering
  AppsMode, LegacyVlessServersCleanup, Placeholder, WgturnPathMigration. v3 / v4
  schema migrations covered. **Gap**: no test for migration *from* a corrupted
  YAML (only happy + targeted-field paths).

## §6 Headless UI coverage

`PageScreenshotTests.cs` captures **9 main pages**:

| Page | Captured? | Baseline (VisualDiffTests)? |
|---|---|---|
| `SubscribePage` | Yes | No |
| `ServersPage` | Yes | No |
| `NetworkPage` | Yes (+ 6 sub-variants: autostart, narrow 720/500/400, routing) | No |
| `ApplicationsPage` | Yes | No |
| `ToolsPage` | Yes | **Yes** (`page-tools.png`) |
| `DpiBypassPage` | Yes | **Yes** (`page-dpi-bypass.png`) |
| `TelegramPage` | Yes (+ 2 narrow + running variants) | **Yes** (`page-telegram.png`) |
| `FreeConfigsPage` | Yes | No (state-varying) |
| `SimplePage` | Yes | No |
| `EmergencyChannelPage` | **No** (page exists in `Views/Pages/`, no screenshot test) | No |

VisualDiff intentionally limits baselines to **static** pages — see
`VPNRouter.Tests/CLAUDE.md`. Cross-platform: Windows-only (font hinting
differs). **Gap**: `EmergencyChannelPage` has no headless test at all — add a
`Capture(new EmergencyChannelPage(), "page-emergency-channel")` line.

## §7 Mocking infrastructure

Missing abstractions hold up unit tests for the 9 UNTESTED services:

- **No `IProcessRunner`** wrapper — every service that shells out to `netsh`,
  `sc`, `ipconfig`, `sing-box`, `winws` (`DnsFlusher`, `HostsManager`,
  `WindowsDnsHardening`, `ZapretActions`, `FirewallManager`, `SingBoxManager`)
  reaches into `Process.Start` directly. Tests cannot intercept stdout / exit
  code without either (a) running the real binary on the dev box, or (b)
  refactoring to inject `Func<ProcessStartInfo, Task<ProcessResult>>`.
  This is the **single highest-leverage abstraction** for v3.0.
- **No `IFileSystem`** — most tests use real `%TEMP%\` paths. Works on
  Windows-only CI; would break on Linux CI if Android-Linux test matrix
  expanded. Suggest System.IO.Abstractions or hand-rolled `IFileSystem`.
- **No `IHttpClient`** — `UpdateChecker`, `SubscriptionFetcher`,
  `ZapretUpdater`, `WgturnUpdater`, `GeoDataDownloader`, `TgProxyUpdater` all
  `new HttpClient()` directly. `SubscriptionFetcherParserTests` tests *parsing*
  but not *fetching*. v3.0 should wrap with `IHttpFetcher` and ship pre-canned
  HTTP fixtures for each downloader.
- **No `ISingBoxApi`** — `SingBoxManager.ReloadConfig` hits Clash API. No way
  to unit-test the hot-reload fallback path.
- **CI graceful-skip pattern** is overused: tests `if (!File.Exists(...))
  return;` instead of `[SkippableFact]`. Migrate to `Xunit.SkippableFact`
  package so CI reports actual skip counts.

## §8 v3.0 refactor enablement

Before splitting `MainWindowViewModel.cs` (6,753 LOC across 7 partials) and
before standing up `VPNRouter.Android` properly, these tests must exist
(estimates use 30 LOC/test rule of thumb):

| Priority | Target | New file | Tests | LOC |
|---|---|---|---|---|
| P0 | `VpnEngine` orchestrator | `VpnEngineLifecycleTests.cs` | 12 (start/stop/restart/apply/escalation matrix) | ~400 |
| P0 | `SingBoxManager` state machine | `SingBoxManagerStopSemanticsTests.cs` | 10 (Stop suppresses Exited, Restart() race, hot-reload fallback) | ~350 |
| P0 | `WindowsDnsHardening` + `HostsManager` | per-service files | 6 + 6 | ~400 (needs `IProcessRunner` first) |
| P1 | `EtwProcessMonitor` | `EtwProcessMonitorTests.cs` | 5 (debounce window, dispose, event ordering) | ~200 |
| P1 | `UpdateChecker` unit mirror | `UpdateCheckerVersionCompareTests.cs` | 8 (`-rN` vs stable semver, channel awareness, hotfix gate) | ~250 |
| P1 | `VlessDeepVerifier` | `VlessDeepVerifierTests.cs` | 8 (handshake decode, timeout, malformed) | ~300 |
| P2 | `ZapretActions` | `ZapretActionsTests.cs` | 7 (Cygwin `SET BIN=`, strategy enum) | ~250 |
| P2 | `MainWindowViewModel` split safety net | `MainWindowViewModelSnapshotTests.cs` (characterization) | 20 (property snapshots before & after refactor) | ~600 |

Total: **~82 new tests / ~2,750 LOC** of test code to land before v3.0
refactor starts. Pure-VM split (MainWindowViewModel) needs **characterization
tests** — capture current public-surface property values for representative
states, then mechanically refactor with snapshot pinning. Process-touching
services need **`IProcessRunner` first**, then **contract tests** asserting
arg vector + exit-code handling.

Also do the §4 `UnitTest1.cs` extraction in parallel — it's mechanical and
unlocks faster test iteration once split.
