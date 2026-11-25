# Phase: Safe Dead-Code and Legacy Cleanup (Workstream 2C)

Date: 2026-11-25
Branch: `dsh/legacy-dead-code-cleanup-20261125`
Base commit: `4e8aa796` (`origin/main`)

## Intent and Scope

Perform a conservative, proof-backed dead-code and legacy cleanup across `VPNRouter.Core/` and `VPNRouter.App/`:

- Remove only files, types, methods, fields, properties, constants, events, localization entries, styles, and duplicate P/Invoke helpers proven to have zero callers/references across the entire repository (`VPNRouter.Core`, `VPNRouter.App`, `VPNRouter.Android`, `VPNRouter.Headless`, `VPNRouter.Headless.Tests`, `VPNRouter.CLI`, `VPNRouter.Service`, `VPNRouter.Tests`, `VPNRouter.GUI`, `packaging/`, `tools/`, `.github/`, and all AXAML/XML/csproj/script files).
- Preserve `MainWindowViewModel` public surface 100% unchanged (no public member, `[ObservableProperty]`, or `[RelayCommand]` added, removed, or renamed).
- Avoid any file overlap with parallel workstreams (`*.csproj`, `DeferredPage.cs`, `MainWindow.axaml.cs`, `App.axaml.cs`, `MainWindowViewModel.RuntimeStatus.cs`, `DiagnosticsExporter.cs`, `plans/OPEN-DEFECTS.md`, or any pre-existing `plans/` file).
- Keep all `[Obsolete]` members still referenced by tests/callers, all JSON/YAML/P/Invoke struct layouts, all Avalonia attached-property accessors, and all multi-edition `#if` directives (`VPNROUTER_BUSINESS`).

## Invariants

- All platform targets (`net10.0` Linux/macOS/Windows `#if PLATFORM_WINDOWS` and `net10.0-android` `#if PLATFORM_ANDROID`) compile cleanly in GitHub Actions CI.
- All unit tests, headless contract checks, Android compile checks, and Windows update/packaging checks pass.
- No local `dotnet build` / `dotnet test` or SDK installation on the development workstation.

## Removed Items (100% Proven Zero-Reference or Pure Duplication)

### Unused File / Type

1. **`VPNRouter.App/ViewModels/AutoSelectStatus.cs`** (`internal static class AutoSelectStatus`, `ResolveSubscribeLabel`):
   - Proof: Full-repo identifier scan confirmed 0 references to `AutoSelectStatus` or `ResolveSubscribeLabel` outside `AutoSelectStatus.cs` (C#, AXAML, csproj, Android, Headless, CLI, Service, Tests, scripts).

### Unused Methods

2. **`VPNRouter.App/Services/WindowsServiceHelper.cs`** (`GetBinPath`, `ResolveServiceExePath`):
   - Proof: 0 references anywhere in repo (including `#if PLATFORM_WINDOWS` callers, `VPNRouter.Service`, `VPNRouter.CLI`, and `VPNRouter.Tests`).
3. **`VPNRouter.Core/Services/UpdateBackup.cs`** (`HasFailureMarker`, `ClearFailureMarker`, `ReadFailureMarker`):
   - Proof: 0 references anywhere in repo; callers and tests use `HasApplicationFailureMarker` and `ReadApplicationFailureMarker`.
4. **`VPNRouter.Core/Services/ConnectionIntentScorer.cs`** (`PickServer`):
   - Proof: 0 references anywhere in repo; callers use `Pick` and `PickRegisteredServer`.
5. **`VPNRouter.Core/Services/SingBoxManager.Health.cs`** (`GetMetrics`):
   - Proof: 0 references anywhere in repo (including `VPNRouter.Tests`; `ProcessMetrics` model stays because `VPNRouter.CLI/Commands/StatusCommand.cs` instantiates it directly).
6. **`VPNRouter.Core/Platform/macOS/MacPrivilegePaths.cs`** (`UserStagedConfigPath`):
   - Proof: 0 references anywhere in repo (including `packaging/macos/` and `MacPrivileged*Tests`).
7. **`VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs`** (`IsRunningAsync` and now-unused `System.Threading` / `System.Threading.Tasks` imports):
   - Proof: 0 references anywhere in repo including `VPNRouter.Android/` and `VPNRouter.Tests/Android/`.

### Write-Only / Unused Fields, Properties, Constants, and Events

8. **`VPNRouter.App/Services/AppAutomationDriver.cs`** (`private static int _port` and write `_port = port`):
   - Proof: Per-type scan confirmed `_port` is only declared (line 29) and assigned (line 79), never read.
9. **`VPNRouter.App/ViewModels/MainWindowViewModel.ServerTesting.cs`** (`private const int ServerDeepConcurrency = 5`):
   - Proof: Private constant with 0 references anywhere in repo; does not affect `MainWindowViewModel` public surface.
10. **`VPNRouter.App/ViewModels/MainWindowViewModel.RollbackAssistance.cs`** (`internal ConnectionRollbackHistory RollbackHistory`):
    - Proof: Internal property with 0 references anywhere in repo (`_rollbackHistory` is accessed directly inside the class); does not affect `MainWindowViewModel` public surface.
11. **`VPNRouter.App/ViewModels/SetupWizardViewModel.cs`** (`ChecksRunningText`):
    - Proof: 0 references in `SetupWizardWindow.axaml`, C#, or tests (`Strings.SetupWizardChecksRunning` remains used directly in `RunChecksAsync`).
12. **`VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs`** (`CountryFlag`, `LatencySortKey`, `IsWorking`, `LatencyColor`, `ErrorTooltip`):
    - Proof: 0 references in `FreeConfigsPage.axaml`, C#, or tests (`FlagFor` and `SortKeyFor` remain used).
13. **`VPNRouter.Core/Services/AutoFailoverEngine.cs`** (`private readonly ConfigSanityCheck _sanity`):
    - Proof: Write-only private field; never read anywhere in `AutoFailoverEngine`. Constructor signature and null guard (`ArgumentNullException.ThrowIfNull(sanity)`) preserved unchanged.
14. **`VPNRouter.Core/Services/EtwProcessMonitor.cs`** (`private readonly IProcessRunner _processRunner`):
    - Proof: Write-only private field inside `#if PLATFORM_WINDOWS`; never read anywhere in `EtwProcessMonitor`. Constructor signature preserved (`_ = processRunner;`) without allocating an unused `ProcessRunner`.
15. **`VPNRouter.Core/Services/HealthMonitor.cs`** (`VpnStarted`, `VpnStopped` events and their 3 dead invocations):
    - Proof: 0 subscribers or references outside `HealthMonitor.cs` across the entire repo (`_vpnWasRunning` state transitions preserved unchanged).
16. **`VPNRouter.Core/Services/LinuxTunOwnership.cs`** (`HeldLockPath` property and 16 unused POSIX `private const`s: `O_WRONLY`, `LOCK_SH`, `S_IFSOCK`, `S_IFBLK`, `S_IFCHR`, `S_IFIFO`, `S_IRWXU`, `S_IXUSR`, `S_IRGRP`, `S_IWGRP`, `S_IXGRP`, `S_IROTH`, `S_IWOTH`, `S_IXOTH`, `MODE_0700`, `ELOOP`):
    - Proof: Per-file and full-repo scans confirmed 0 code references (`_heldLockPath` field stays because `Release` logs it; `S_IRWXU` was only used by unused `MODE_0700`).
17. **`VPNRouter.Core/Services/VpnEngine.cs`** (`IsTrueSplitEngaged`):
    - Proof: 0 references anywhere in repo.
18. **`VPNRouter.Core/Services/CanaryPolicy.cs`** (`DirectProbesDefaultEnabled`):
    - Proof: 0 references anywhere in repo.
19. **`VPNRouter.Core/Services/PlaceholderDefense.cs`** (`KnownPubkeys`, `KnownShortIds`, `KnownServers`):
    - Proof: 0 references anywhere in repo (private backing sets remain used by `Inspect`).
20. **`VPNRouter.Core/Platform/macOS/MacPrivilegePaths.cs`** (`ManifestPath`, `InstallLockPath`, `LegacyPolicyPath`):
    - Proof: 0 references anywhere in repo.
21. **`VPNRouter.Core/Platform/macOS/MacPrivilegedClient.cs`** (`IsSupportedPlatform`):
    - Proof: 0 references anywhere in repo.
22. **`VPNRouter.Core/Platform/macOS/MacFirewallManager.cs`** (`internal bool IsAnchorMode`):
    - Proof: 0 references anywhere in repo (`_anchorMode` field stays because internal firewall teardown uses it).

### Unused Localization Entries (12 Core Keys + 7 Redundant App Forwarders)

23. **`VPNRouter.App/Localization/Strings.cs` + `VPNRouter.Core/Localization/Strings.cs`**:
    - `RuleParserMissingValue`, `RuleParserUnknownType(string)`, `RulesImportWithWarnings(int)` — 0 references outside `Localization/`.
24. **`VPNRouter.App/Localization/Strings.cs` + `VPNRouter.Core/Localization/Strings.Telegram.cs`**:
    - `TgProxyDownloadStep1Python`, `TgProxyDownloadStep2Wheels`, `TgProxyDownloadStep3Source`, `ZapretOneTapNoSignalToast` — 0 references outside `Localization/`.
25. **`VPNRouter.Core/Localization/Strings.Assistant.cs`**:
    - `AssistantApply`, `AssistantStepLimit` — 0 references outside `Localization/`.
26. **`VPNRouter.Core/Localization/Strings.ServerHealth.cs`**:
    - `HealthAsnHighRisk`, `HealthYoutubeCanaryCaveat`, `HealthCanaryPartialNote` — 0 references outside `Localization/`.

### Unused Style Selector and Duplicate Reduced-Motion P/Invoke Helpers

27. **`VPNRouter.App/Styles/HomeKit.axaml`** (`Border.hk-divider` style):
    - Proof: `hk-divider` has 0 references anywhere in AXAML, C#, or tests.
28. **`VPNRouter.App/Controls/StatusEmblem.axaml.cs` and `VPNRouter.App/Views/Pages/SimplePage.axaml.cs`**:
    - Removed two duplicate `private static bool PrefersReducedMotion()` methods and their duplicate `user32.dll` `SystemParametersInfo` P/Invoke declarations; both now reuse `!UiMotion.Allowed()` from `VPNRouter.App/Services/UiMotion.cs` (identical Windows `SPI_GETCLIENTAREAANIMATION` check, plus consistent `VPNROUTER_REDUCED_MOTION=1` support).

## Explicitly Kept (Verified Live, Side-Effectful, or Contract-Bound)

- `FirewallManager.ConsoleEncoding` / `ResolveConsoleEncoding()` / `GetOEMCP()`: kept per `plans/phase-h6-unused-members-2026-09-29.md` because its static initializer registers `CodePagesEncodingProvider.Instance`.
- `VpnEngine` `[Obsolete]` constructor: still invoked by `PlatformServices.CreateVpnEngine` and 12 test suites.
- `RollbackArtifactPolicy.cs` `#if VPNROUTER_BUSINESS`: intentional cross-edition flag shared with the `business` branch.
- `VPNRouter.App/Assets/penguin_mascot_white.png`: source artwork consumed by `tools/icons/make-icons.py`.
- `VPNRouter.App/Styles/ControlRoles.axaml` (`ControlCornerRadius`, `OverlayCornerRadius`) and `Tokens.axaml` scale tokens: Avalonia FluentTheme overrides and design-system scale tokens.
- JSON/YAML DTO and P/Invoke struct layout members (`VPNConfig.DownloadDetour`, `VPNConfig.Inbound`, `ClashSingBoxApi.DownloadTotal`/`UploadTotal`, `SpDevInfoData`, `NOTIFYICONDATAW`, `SplitTunnelDriverInterop`, `ProcessOwnership`).

## Larger Legacy Refactor Candidates (Not Removed Here; Owner Decision Needed)

These require breaking public/XAML contracts, removing test-only production helpers (along with their tests), or consolidating shared logic across files:

1. **`MainWindowViewModel` unused public surface (~65 members)**:
   - ~55 unused `L_*` / `Lbl*` label forwarders in `MainWindowViewModel.Localization.cs`, `MainWindowViewModel.LocalizedLabels.cs`, `MainWindowViewModel.SimpleMode.cs`, and `MainWindowViewModel.Zapret.cs`, plus unused `VersionText` and `IsLinuxPlatform`.
   - 10 unused `[RelayCommand]` methods whose generated `*Command` properties have zero XAML/C# references: `RemoveCustomApps`, `CloseCustomConfigDetail`, `OpenServerDetail`, `OpenCustomConfigDetail`, `ToggleTheme`, `ApplySettings`, `ClearSubscription`, `ToggleZapretAsync`, `ExpandZapretTuneSection`, `UpdateZapretHostsAsync`.
   - Pruning these also unlocks removing dozens of now-redundant `VPNRouter.App/Localization/Strings.cs` forwarders and `VPNRouter.Core/Localization/` entries.
2. **Test-only production helpers (zero production callers, referenced only by `VPNRouter.Tests`)**:
   - `VPNRouter.App/ViewModels/ActiveServerMatcher.cs` (`FindName`)
   - `VPNRouter.Core/Services/SuffixMatch.cs` (`LongestSuffixIndex`)
   - `VPNRouter.Core/Services/ProfileApplication.cs` + `BuiltInAndroidProfiles.cs`
   - `VPNRouter.Core/Platform/Unix/MacDnsParsers.cs` (`ParseGetDnsServers`)
   - `VPNRouter.Core/Platform/macOS/MacPrivilegePolicy.cs` (`LooksLikeLegacyPolicy`)
   - `VPNRouter.Core/Services/ConnectConsentGate.cs` (`TryOpen`)
   - `VPNRouter.Core/Services/ConnectionHealthState.cs` (`SetActiveNode`, `SnapshotByNode`)
   - `VPNRouter.Core/Services/ServerHealthProbe.cs` (`PickForConnect`)
   - `VPNRouter.Core/Services/SubscriptionAssistantClient.cs` (`PlanAsync`)
   - `VPNRouter.Core/Services/WindowsServiceCommand.cs` (`IsRecognizedVpnRouterImagePath`)
   - `VPNRouter.Core/Services/ZapretActions.cs` (`GetGameFilterMode`)
   - `VPNRouter.App/ViewModels/SubscriptionRefreshDiff.cs` (`ActiveServerSignature`)
   - `VPNRouter.App/Services/WindowsRollbackRelaunch.cs` (`RestoreAndStart`)
3. **Duplicate outbound builders and helpers across Core**:
   - `ConfigGenerator.OutboundBuilders.cs` vs `VlessDeepVerifier.cs` duplicate `BuildVlessOutbound`, `BuildVmessOutbound`, `BuildHysteria2Outbound`, `BuildTuicOutbound`, `BuildShadowsocksOutbound`, `BuildNaiveOutbound`.
   - `VlessDeepVerifier.cs` vs `FreeConfigDeepVerifier.cs` duplicate `BuildSingleOutboundConfig`.
   - `FirewallManager.ConsoleEncoding` static code-page registration: confirm whether any dependency on Windows relies on `CodePagesEncodingProvider` before removing `ConsoleEncoding` / `ResolveConsoleEncoding` / `GetOEMCP`.
4. **`VpnEngine` `[Obsolete]` constructor migration**:
   - Migrate the 12 test suites off the `[Obsolete]` `new VpnEngine(...)` constructor (or convert it to `internal`) so the Phase 3 deprecation warning can be retired.

## Verification

- Local: post-edit static verification via Python audit/reference checks (zero remaining references to removed symbols; zero overlap with restricted files; `MainWindowViewModel` public surface diff checked).
- Remote CI (exact-head PR checks on `dsh/legacy-dead-code-cleanup-20261125`):
  - `dotnet test` (`test.yml`: Linux Release build, unit tests, headless publish, headless contract checks, arch packaging)
  - `Android compile check` (`android-compile.yml`: `net10.0-android` compilation and Android Java contract tests)
  - `test-windows-update.yml`, `macos-privileged.yml`, `grep-placeholder-fingerprints.yml`

## Outcome

Pending PR creation and exact-head CI verification.
