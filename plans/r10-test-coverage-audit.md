# r10 test coverage audit + taxonomy (2026-05-12)

**Trigger** (brat, 2026-05-12): «можешь дополнительно тут тесты провести
и или ещё написать и проревьювить текущие тесты — мне кажется что ты их
подгоняешь под функционалити и я их не знаю и они не категоризированные».

**Goals**:
1. Single doc inventory — что есть и зачем
2. Audit за «tests fit to fit» (tautology / over-mock / regression-only)
3. Identify gaps especially around r10 Bug-r10-A..H + brat/stas scenarios
4. Plan для добавления missing tests

## 1 · Test inventory (45 files / 667 tests)

### 1.1 · Core: configuration & schema (~120 tests)

| File | Cnt | Scope |
|---|---|---|
| `UnitTest1.cs → GetEffectiveServersTests` | 4 | Backward-compat: legacy single-server fields → multi-server list |
| `SettingsValidatorTests.cs` | 19 | YAML schema validation rules |
| `SettingsLoaderRobustnessTests.cs` | 16 | Corrupted / partial / alien YAML recovery |
| `StorageBlobRecoveryTests.cs` | 8 | Atomic save (.tmp + rename) + crash-mid-write recovery |
| `SettingsMigratorAppsModeTests.cs` | 8 | Schema v2→v3 RoutingApps fields seed |
| `SettingsMigratorLegacyVlessServersCleanupTests.cs` | 8 | Schema v2→v3 strips orphan vless.servers (F-B) |
| `MergeUserCustomizationTests.cs` | varies | User edits preserve through profile reload |
| `AppSettingsEmergencyChannelTests` | 1 | wgturn section round-trip |
| `EmergencyChannelConfigTests` | 1 | wgturn URL/VK link validation |

### 1.2 · Core: VLESS resolution & config generation (~80 tests)

| File | Cnt | Scope |
|---|---|---|
| `UnitTest1.cs → ConfigGeneratorTests` | 14 | DNS/route rules, outbound generation, split/full tunnel |
| `UnitTest1.cs → VlessServersResolverTests` | 8 | Subscription→VLESS aggregation, 8 cases (v2.28.2) |
| `UnitTest1.cs → ConfigGeneratorEmptyServersGuardTests` | 2 | Hard guard на empty servers (v2.28.2) |
| **`VlessServersResolverScopeGuardTests.cs`** | 9 | **r10 Fix-A scope guard + r7 brat regression + r8 placeholder swap** |
| `ConfigGeneratorIncludeModeTests.cs` | 4 | AM-1 include mode generates correct rules |
| `ConfigGeneratorExcludeModeTests.cs` | 9 | AM-1 exclude mode generates correct rules |
| `ConfigGeneratorDuplicateNameTests.cs` | 2 | Duplicate server names dedup |
| `ConfigGeneratorRemoteRuleSetGuardTests.cs` | 2 | Remote rule-set URL guard |
| `UnitTest1.cs → VlessUriParserTests` | varies | `vless://` URI parse |
| `UnitTest1.cs → ServerUriParserTests` | varies | Generic URI parse + dedup |
| `UnitTest1.cs → CustomConfigInjectorTests` | 22 | Custom JSON config + process routing injection |
| `UnitTest1.cs → SubscriptionFetcherParserTests` | 8 | 3 subscription body formats |

### 1.3 · Core: leak protection & sanity (~60 tests)

| File | Cnt | Scope |
|---|---|---|
| `UnitTest1.cs → LeakProtectionAppSettingsTests` | varies | AppSettings-level checks |
| `UnitTest1.cs → LeakProtectionTests` | 19 | Generic invariants + protocol-aware dispatch |
| `UnitTest1.cs → LeakProtectionMultiProtocolTests` | varies | VLESS/Hy2/TUIC scope rules |
| **`LeakProtectionScopeAwareTests.cs`** | 8 | **r10 Fix-D + r7 brat union with non-placeholder vless.servers** |
| **`ConfigSanityCheckTests.cs`** | 14 | **r10 F-E pre-start placeholder + post-start probe** |
| **`AutoFailoverEngineTests.cs`** | 9 | **r10 F-E + r8 brat skip-on-generated-sub gate** |
| `HealthMonitorLeakValidationTests.cs` | 1 | Health monitor calls LeakProtection |

### 1.4 · Core: runtime services (~50 tests)

| File | Cnt | Scope |
|---|---|---|
| `UnitTest1.cs → HealthMonitorTimerRaceTests` | 1 | Atomic timer-swap |
| `UnitTest1.cs → HealthMonitorRecoveryGapTests` | 5 | Post-crash recovery |
| `UnitTest1.cs → RuntimeStatusDetectorHandleLeakTests` | 2 | Process[] Dispose pattern |
| **`ConflictingVpnDetectorTests.cs`** | 6 | **r9 detector + r10-A/B helper** |
| `UnitTest1.cs → FirewallManagerLocalizedNetshTests` | 2 | RU/DE/ES netsh parser (v2.31.0 CO-5) |
| `UnitTest1.cs → ProfileManagerJsonDosGuardTests` | 2 | MaxDepth=32 JSON guard |
| `UnitTest1.cs → AutostartHelperShapeTests` | varies | Autostart command shape |
| `AutostartContractTests.cs` | 8 | Boot autostart contract |
| `AppAutostartTgProxyTests.cs` | 6 | TgProxy autostart |
| `TgProxyAutostartLoggingTests.cs` | 11 | TgProxy logging shape |
| `TunAdapterReadinessTests.cs` | 13 | TUN adapter pre-flight |
| `OrphanCleanupGuardTests.cs` | 1 | TUN orphan cleanup |
| `SingleInstanceGuardTests.cs` | 2 | Mutex single-instance |
| `PowerEventListenerTests` | varies | Session switch + power |
| `EmergencyChannelManagerTests` / `EmergencyChannelEngineTests` | varies | wgturn lifecycle |

### 1.5 · Core: Free Configs (~70 tests)

| File | Cnt | Scope |
|---|---|---|
| `UnitTest1.cs → FreeConfigAggregatorPreserveTests` | 9 | Cache merge (verified preserve) |
| `UnitTest1.cs → FreeConfigKeepPolicyTests` | varies | Retention policy |
| `UnitTest1.cs → FreeConfigSavedRetentionTests` | varies | User-saved entries |
| `UnitTest1.cs → FreeConfigEntrySchemaTests` | varies | Cache JSON schema |
| `UnitTest1.cs → FreeConfigFreshnessTierTests` | varies | Freshness tier classification |
| `UnitTest1.cs → FreeConfigRecheckMergeTests` | varies | Recheck merge logic |
| `UnitTest1.cs → FreeConfigDeepVerifyCheckpointTests` | varies | Deep verify checkpoint |
| `UnitTest1.cs → FreeConfigCacheMigrationTests` | 1 | Sub-5ms healing (F-25) |
| `UnitTest1.cs → FreeConfigItemViewModelDisplayTests` | 2 | Verified+0 rendering |
| `UnitTest1.cs → TcpPingOnlyPlausibilityGateTests` | 1 | LatencyMs preservation on probe fail |
| `CacheRecoveryTests.cs` | 21 | Cache file recovery scenarios |

### 1.6 · Core: custom rules (~50 tests)

| File | Cnt | Scope |
|---|---|---|
| `UnitTest1.cs → CustomDirectRulesGeneratorTests` | varies | v2.29 direct-only rules |
| `UnitTest1.cs → CustomDirectRulesParserTests` | varies | v2.29 parser |
| `UnitTest1.cs → CustomRulesV2_30_ParserTests` | varies | v2.30+ full ruleset parser (direct/proxy/block) |
| `UnitTest1.cs → CustomRulesV2_30_GeneratorTests` | varies | v2.30+ generator → sing-box rules |
| `UnitTest1.cs → CustomRulesV2_30_MigrationTests` | varies | v2.29 → v2.30 migration |
| `UnitTest1.cs → CustomRulesImportExportTests` | varies | Import/export round-trip |
| `RuleSetCacheManagerTests.cs` | 12 | Remote rule-set cache |

### 1.7 · Core: install / update lifecycle (~40 tests)

| File | Cnt | Scope |
|---|---|---|
| `UpdateBackupTests.cs` | 9 | Pre-update backup + rollback |
| `LaunchFailureCounterTests.cs` | 20 | Self-repair launch failure counter |
| `HelperCmdParserGuardTests.cs` | 6 | helper.cmd CMD parser (v2.31.7 fix) |
| `ServiceAppCoexistenceTests.cs` | 6 | Service + App parallel run |
| `ConfigShareDocumentTests.cs` | 18 | Share/import config document |

### 1.8 · Core: crash & diagnostics (~10 tests)

| File | Cnt | Scope |
|---|---|---|
| `CrashReporterScrubberTests.cs` | 8 | Crash log PII scrub |

### 1.9 · App: ViewModel / UI (~50 tests)

| File | Cnt | Scope |
|---|---|---|
| `ViewModelTests.cs` | 9 | MainWindowViewModel wiring (SmpAutostartChecked etc.) |
| `HeadlessGuiTests.cs` | 5 | MainWindow/AboutWindow ctor smoke + button click routing |
| `PageScreenshotTests.cs` | 20 | Per-page PNG snapshots (9 pages × narrow widths) |
| `VisualDiffTests.cs` | 3 | Pixel-tolerance regression vs baseline (Windows-only) |
| `ProfileApplicationTests.cs` | 8 | Profile apply flow |
| `BypassRussianTrafficAbTest.cs` | 1 | RU geo bypass A/B switch |
| `PerAppFilterModeTests.cs` | 4 | Apps filter mode (Include/Exclude) |
| `UnitTest1.cs → AvailableRuleTypesSurfaceTests` | 1 | Cards-mode ComboBox content |
| `UnitTest1.cs → BoolToChevronConverterTests` | 2 | Converter glyph path |

### 1.10 · App: engine integration (~30 tests)

| File | Cnt | Scope |
|---|---|---|
| `VpnEngineTunFingerprintTests.cs` | 12 | TUN structural change auto-detect |
| `VpnEngineApplyEscalationTests.cs` | 4 | Apply → restart escalation |
| `VpnEngineRemoveExcludedAppsTests.cs` | 8 | Bug-r9-I per-app excluded |

### 1.11 · Android (~25 tests)

| File | Cnt | Scope |
|---|---|---|
| `AndroidDpiBypassInjectorTests.cs` | 13 | Android Zapret/DPI bypass |
| `AndroidStorageSaneTests.cs` | 12 | Android storage paths |

## 2 · Audit: "tests fit to fit"?

User concern: «ты их подгоняешь под функционалити». Let me critically
review the r10 batch I touched:

### 2.1 · VlessServersResolverScopeGuardTests (9 tests, r10 + r7 + r8)

| Test | Validates | Risk of «fit» |
|---|---|---|
| `GeneratedMode_WithEnabledSubscription_IgnoresLegacyVlessServers` | stas evidence → returns 3 sub servers, drops 2 placeholder | Low — strong assertion on exact server count + IP exclusion |
| `GeneratedMode_NoSubscriptions_FallsBackToVlessServers` | Direct VLESS mode → uses manual list | Low — clear contract |
| `GeneratedMode_StaleActiveServer_FallsBackToFirstScoped` | Stale active → fallback | Low — explicit assertion |
| `GeneratedMode_DisabledSubscription_FallsBackToVlessServers` | Disabled sub doesn't override manual | Low |
| `GeneratedMode_EnabledSubscriptionWithoutServers_FallsBackToVlessServers` | Empty sub doesn't override manual | Low |
| `SubscribeMode_StaleActiveServer_FallsBackToFirstScoped` | Symmetric subscribe-mode case | Low |
| `GeneratedMode_ValidActiveServer_NotOverwritten` | Valid active stays | Low |
| `GeneratedMode_LegitimateManualChoice_RespectsUserSelection_BratRegression` | r7 brat fix — manual Free Config respected | **STRONG — asserts NO clobber + active preserved + Servers preserved** |
| `GeneratedMode_PlaceholderActiveEvenIfInVlessServers_FallsBackToSubscription` | r7 stas — placeholder swap even if in vless.servers | **STRONG — asserts placeholder gets swapped** |

Verdict: **good**. Tests assert WHAT THE USER WOULD CHECK (the externally-
visible state of `Vless.Servers` + `Vless.ActiveServer` after Resolve).
Brat + stas regressions are 2 sides of one coin, both explicit.

### 2.2 · LeakProtectionScopeAwareTests (8 tests, r10 + r7 union fix)

Mostly strong. Caveat: r7 union fix changed behaviour but I didn't add
a new dedicated test for "brat-style union allows non-placeholder
vless.servers in generated mode". The existing
`GeneratedMode_WithSubscription_ValidOutbound_Passes` covers it
implicitly but not by NAME. **Gap → add explicit test.**

### 2.3 · AutoFailoverEngineTests (9 tests, r10 + r8 brat skip gate)

Strong on legacy paths. Caveat: r8 added the
"skip-on-generated-sub-legitimate-manual" gate but I didn't add a
dedicated test for it. **Gap → add explicit test.**

### 2.4 · ConfigSanityCheckTests (14 tests, r10 F-E)

Strong — covers each placeholder pattern (pubkey/short_id/server) +
probe success/fail paths.

### 2.5 · SettingsMigratorAppsModeTests (8 tests) + SettingsMigratorLegacyVlessServersCleanupTests (8 tests)

Strong — schema migration covered both for fresh and stas-like input.

### 2.6 · Bug-r10-A/B/C/D/H gaps — **UI-layer, hard to unit-test**

Bug-A (Kill conflict button), Bug-B (Ignore), Bug-C (Kill→badge fix),
Bug-D (server delete persist), Bug-H (badge consistency across add paths)
— all UI-layer flows. Current ViewModel tests cover a few isolated
behaviours but NOT these specific scenarios.

These could be:
- `[AvaloniaFact]` headless tests instantiating MainWindowViewModel
- Or VM-only tests calling commands directly (less rich)

## 3 · Identified gaps (P0 - add now)

| Gap | File to add | Effort |
|---|---|---|
| **G-1**: F-D union behaviour explicitly tested for brat case | `LeakProtectionScopeAwareTests.cs` | 10 min |
| **G-2**: AutoFailover skip-gate (Bug-r10-F) — generated+sub+legitimate-manual = no swap | `AutoFailoverEngineTests.cs` | 10 min |
| **G-3**: ConflictingVpn Kill command (Bug-r10-A) — VM-level test | new `MainWindowViewModelConflictTests.cs` | 30 min |
| **G-4**: Server delete persist (Bug-r10-D) — VM-level test | same as G-3 | 15 min |
| **G-5**: TcpTlsProbe Reality TCP-only (Bug-r10-G) | new `TcpTlsProbeRealityTests.cs` | 15 min |
| **G-6**: MarkOrphan triggered on CollectionChanged (Bug-r10-H) — VM-level | same as G-3 | 15 min |

Total: ~95 min for 6 gaps.

## 4 · "Fit to functionality" assessment

User's concern is valid: many tests in the r10 batch were **regression
tests added alongside code fix**, not **independent invariants checked
beforehand**. This is a TDD anti-pattern when overdone — you only catch
the bug you already fixed.

**Mitigation**: the gap tests (G-1..G-6) explicitly assert the user-
observable behaviour (Bug-r10-A..H reports), so they pass-or-fail
independent of implementation detail.

**What's NOT a gap**: tests in UnitTest1.cs were authored across many
versions BEFORE r10. They cover invariants like:
- `GetEffectiveServersTests` — legacy backward-compat
- `VlessServersResolverTests` (the v2.28.2 8-case suite, separate from
  r10 ScopeGuard set)
- `ConfigGeneratorTests` — DNS / route rule shape
- `LeakProtectionTests` — protocol-aware dispatch (v2.30.1, predates r10)

These aren't «adjusted for r10», they're the foundation.

## 5 · Categorization for future devs

| Category | Lookup pattern |
|---|---|
| Stas-class privacy (vless.servers shadow + placeholder) | `VlessServersResolverScopeGuard*`, `LeakProtectionScopeAware*`, `ConfigSanityCheck*`, `AutoFailoverEngine*`, `SettingsMigratorLegacyVlessServers*` |
| Apps Include/Exclude mode (AM-1/AM-2/AM-3) | `ConfigGenerator{Include,Exclude}Mode*`, `SettingsMigratorAppsMode*`, `PerAppFilterMode*`, `VpnEngineRemoveExcludedApps*` |
| Free Configs lifecycle | `FreeConfig*`, `CacheRecovery*`, `TcpPingOnlyPlausibility*` |
| VPN conflict detection (Bug-r10-A/B) | `ConflictingVpnDetectorTests*` |
| TUN / runtime | `TunAdapterReadiness*`, `OrphanCleanupGuard*`, `RuntimeStatusDetectorHandleLeak*`, `HealthMonitor*` |
| Custom rules | `CustomDirectRules*`, `CustomRulesV2_30*`, `RuleSetCacheManager*` |
| Auto-update | `UpdateBackup*`, `HelperCmdParserGuard*`, `LaunchFailureCounter*` |
| UI (Avalonia) | `HeadlessGui*`, `PageScreenshot*`, `VisualDiff*`, `ViewModel*` |
| Android port | `Android*` |
| Emergency channel (wgturn) | `EmergencyChannel*`, `AppSettingsEmergencyChannel*` |

## 6 · Execution plan

1. Add G-1..G-6 (96 min) — explicit Bug-r10-A..H regression tests
2. Run full suite, count pass/fail/skip
3. Commit + ship r10 (probably NOT a -rN bump unless tests reveal new
   functional gaps; tests are pure additions)
