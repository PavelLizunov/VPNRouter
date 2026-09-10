# Night Audit Final Independent Criteria Report (2026-09-05)

**Base Commit**: `b7ce0e4f140b7ed4257673aa67a2b359c535ef7f` | **Fixed HEAD**: `489a529b16efec978908964c8c74e5f6bf2c785f` | **Product Changes**: No product changes since reviewed/tested HEAD `489a529b`; documentation-only follow-up (docs commit `a027dad9`, all 4 CI checks green; product code last tested at `489a529b` unaffected by docs-only changes)
**Final HEAD CI**: Run [33974410438](https://github.com/PavelLizunov/VPNRouter/actions/runs/33974410438) (3 jobs) + separate Run [33974410445](https://github.com/PavelLizunov/VPNRouter/actions/runs/33974410445) (grep) (All 4 checks GREEN, not all 4 jobs in run 33974410438) | **Overall Verdict**: `implementation+mockCIverified` for all 12 IDs
**Scope & Witness Tally**: 12/12 IDs verified (11 behavioral + 1 source wiring; 14 witness cases: 02/03/07/09/10 [5], 04 [2], 11 [1], 01 [1], 12 [1], 08 [1], 05 [2], 06 [1] + 2 GREEN/GREEN controls for 05).
**Ledger Status & Follow-ups**: Only the 12 approved night audit defects (NIGHT-01..12) are resolved in PR #240 in `plans/OPEN-DEFECTS.md`; two preexisting follow-ups (`NIGHT-FOLLOWUP-01` and `NIGHT-FOLLOWUP-02`) remain **OPEN P1**; release semantics unchanged; product code last tested at `489a529b` unaffected by docs-only changes.

---

## 1. Governance, Invariants & Environmental Boundaries

1. **Evidence Boundary**: All evidence verified against unit/characterization tests, mock runners, synthetic fixtures, loopback sockets, and GitHub Actions CI (`test.yml`). Full-solution unfiltered execution, headless UI screenshots, native tun/Wintun drivers, live packet filters (`nft`/`pf`), live process termination, and live WebSocket telemetries were **NOT EXECUTED** (excluded authority; no local .NET SDK or elevated kernel environment).
2. **Evaluation Invariant**: Scoped RED/GREEN witness tests prove reproduction and repair of targeted defects within mock CI seams; they do NOT constitute full solution, native platform, or release acceptance.
3. **Selector Atomicity Boundary**: For NIGHT-06, tests verify the await-window rollback race; there is **NO proof of full same-object cross-thread selector atomicity** (tests only cover await-window).
4. **Wiring & Seam Precision**: NIGHT-12 is verified via source wiring inspection and synthetic Serilog sink capture, **not** a live WebSocket handshake. NIGHT-05 cold startup ordering is verified via source guard only (`StartupPipeline_ColdOrderingSourceGuard...`), **not** live cold startup execution. NIGHT-07 UI promotion tests use uninitialized reflection fixtures (`RuntimeHelpers.GetUninitializedObject`), **not** live Avalonia UI window constructors.
5. **Platform Facts**: `ProcessHandle.Pid` (`ProcessRunner.cs:213`) is an in-memory cached property, not an OS query (`fakePidThrow` was an artificial artifact). `MainWindowViewModel` baseline public surface hash is preserved by strictly excluding approved additive private handler `OnEngineConnected`.

---

## 2. Verbatim CI Workflow Commands & Execution Counts

From `.github/workflows/test.yml` (current workflow commands verbatim; HEAD CI Run `33974410438`, 3 jobs):
- **Ubuntu Regression Job (`test`)**:
  - `dotnet restore VPNRouter.Tests/VPNRouter.Tests.csproj`
  - `dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-restore`
  - `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff" --logger "trx;LogFileName=test-results.trx" --logger "console;verbosity=normal"`
- **Windows Repair Stub Job (`go-test-windows`)**:
  - `go test ./...` (working-directory: `VPNRouter.GUI`)
- **Windows Characterization Job (`characterization-windows`)**:
  - `dotnet publish VPNRouter.CLI/VPNRouter.CLI.csproj -c Release -r win-x64 --self-contained false -o "$env:RUNNER_TEMP\vpnrouter-cli"`
  - `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~Characterization|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"`

From `.github/workflows/grep-placeholder-fingerprints.yml` (separate HEAD CI Run `33974410445`, 4th check):
- **Placeholder Fingerprints Job (`grep`)**:
  - Verifies single-source-of-truth for placeholder fingerprints across production code (`grep -rln ... | grep -vE ...`).

**Witness Run Test Counts**:
- `/tmp/vpnrouter-failover-witness-green.log` (HEAD CI Run `33974410438`, Ubuntu): Total tests: 3352 | Passed: 3277 | Skipped: 75 | Failed: 0.
- `/tmp/vpnrouter-stop-witness-green.log` (Known Windows Characterization Log, older PR #240 merge commit SHA `76d98f618c61ca0480be9001a890a2ffdfc80017`, not misattributed): Total: 69 | Passed: 69 | Skipped: 0 | Failed: 0.

---

## 3. Independent Reviewer Synthesis & Lead Corrections

Three reviewers reported no introduced blockers; lead spot-checked source and corrected citations; lead accepts approved 12 implementation + mock CI scope on tested `489a529b` (runs 33974410438 + grep 33974410445) and scoped RED/GREEN matrix (only the 12 defects are resolved in PR #240; 2 P1 follow-ups remain OPEN):
- **Slice 01/07/08**: Reviewer cited non-existent test names (`Constructor_WiresDurableConnectedHandler...`, `AutoFailover_Recreation...`). Lead corrected citations to actual methods: `Stopped_LegacyStrings_CannotSetIsConnected_TypedCurrentConnected_SetsIsConnectedTrue` (uninitialized reflection fixture) and `Subscription_And_Unsubscription_SourceGuard`.
- **Slice 02/03/04/05**: Reviewer cited invented test name `ApplyAsync_HotReloadSucceeds_OnePutAndFirewallCapabilityCalled`. Lead corrected citation to real test name: `ApplyAsync_HotReloadSucceeds_CallsFirewallCapabilityOnceWithExactGeneratedAndIntent`. Cold startup verified via source guard only.
- **Slice 06/09/10/11/12**: Reviewer evaluated NIGHT-06 prior to matrix update. Lead confirmed NIGHT-06 executed on baseline run `33974662105` (`Assert.Null` failed on captured old failover pool after Stop, receipt `5552819799`), and passed on HEAD run `33974410438`. Clarified that selector atomicity tests only cover await-window rollback, and NIGHT-12 is source wiring/logging, not live handshake.

---

## 4. Twelve-Defect Criteria, Implementation & Evidence Matrix

### NIGHT-01 — TgProxy Ownership & No Port Adoption (P1)
- **Criterion & Code**: Eliminate destructive sweeps; safe no-op `KillAll`/`KillByPort`; quit/toggle call owned `_tgProxy.Stop()`; preserve handle and secret on failed stop; zero port-only truth. `TgProxyManager.cs:542–598`, `MainWindowViewModel.cs:7031`, `RuntimeStatusDetector.cs:107–111`.
- **Test Evidence & Classification**: `NightBaselineOwnershipCharacterizationTests.cs`: `Night01_TgProxyManager_FailedStop_PreservesExactHandleAndActiveSecret` (Behavioral mock/handle seam); `TgProxyOwnershipCharacterizationTests.cs`: `TgProxyManager_KillAll_Structural_NoDestructiveCalls` (Structural / source guard), `OwnedManager_PositiveStop_CallsKillAndSuppressOnOwnedHandle` (Behavioral mock/handle seam). *Source Guard & Behavioral (separated: structural source guard + behavioral mock/handle seam; not all behavioral; zero live process sweeps)*.
- **RED Witness**: Run [33971648760](https://github.com/PavelLizunov/VPNRouter/actions/runs/33971648760) (commit `0d757660`): `Assert.Same` failed (handle was nulled on failed stop instead of retained).
- **GREEN Witness**: Run [33971405494](https://github.com/PavelLizunov/VPNRouter/actions/runs/33971405494) (commit `0072c571`, receipt [5552469312](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552469312)).

### NIGHT-02 — Custom WireGuard Endpoint DNS Detour Preservation (P1)
- **Criterion & Code**: Unified destination classification across outbounds & endpoints; prevent stripping WG endpoint detours to `dns-direct`; unknown tags fail closed. `CustomConfigInjector.cs:895–959, 1515–1536`.
- **Test Evidence & Classification**: `NightBaselineRegressionTests.cs`: `Night02_CustomConfigInjector_WireGuardEndpointDns_PreservesWgDetour`; `NightDnsPrivacyRegressionTests.cs`: `CustomConfigInjector_FullInject_PlainWireGuardEndpoint_SplitInclude_DetourRemainsWg`. *Behavioral (AST/JSON seam)*.
- **RED Witness**: Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431) (commit `0a876da6`): Expected `"wg"`, Actual `"dns-direct"`.
- **GREEN Witness**: Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449) (commit `c217389b`).

### NIGHT-03 — StrictDns Precedence Over Smart-DNS Process Rules (P1)
- **Criterion & Code**: `StrictDns` overrides smart-DNS mode on process include/exclude rules to route through `"vpn-dns"`; failover override preserved. `ConfigGenerator.Dns.cs:32, 141–173`.
- **Test Evidence & Classification**: `NightBaselineRegressionTests.cs`: `Night03_ConfigGenerator_StrictDnsOverridesSmartMode_RoutesVpnDns`; `NightDnsPrivacyRegressionTests.cs`: `CustomConfigInjector_CustomExclude_StrictDns_TogglesProcessDnsRemote`. *Behavioral (in-memory config)*.
- **RED Witness**: Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431) (commit `0a876da6`): Expected `"vpn-dns"`, Actual `"local-dns"`.
- **GREEN Witness**: Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449) (commit `c217389b`).

### NIGHT-04 — Unix Firewall Failed Cleanup Retains Recovery Marker (P1)
- **Criterion & Code**: Failed `nft`/`pf` cleanup retains recovery marker; Linux verifies table absence via JSON AST; Mac retains marker on failed anchor flush; operations under `_gate` lock. `LinuxFirewallManager.cs:53, 143–330, 587–610`, `MacFirewallManager.cs:64, 195–270, 665–750`.
- **Test Evidence & Classification**: `NightBaselineFirewallTests.cs`: `Night04_Linux_CleanupOrphanedRules_FailedDelete_RetainsMarker_AndRecoveredSuccess_ClearsMarker`, `Night04_Mac_CleanupOrphanedRules_FailedAnchorFlush_RetainsMarker_AndRecoveredSuccess_ClearsMarker`. *Behavioral (2 witness cases, file/mock seam)*.
- **RED Witness**: Run [33970137690](https://github.com/PavelLizunov/VPNRouter/actions/runs/33970137690) (commit `5f718912`): `Assert.True(File.Exists)` failed on Linux & Mac.
- **GREEN Witness**: Run [33969900698](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969900698) (commit `77b96a1b`, receipt [5552295438](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552295438)).

### NIGHT-05 — Endpoint Completeness & Committed Firewall Integration (P1)
- **Criterion & Code**: `ICommittedFirewallConfig` capability; extracts outbound + WG peer IPv4/IPv6; Phase 6 skips legacy, Phase 8 commits; Apply atomic refresh; disarm before DNS. `IFirewallManager.cs:28–31`, `LinuxFirewallManager.cs:420–560`, `MacFirewallManager.cs:500–640`, `StartupPipeline.cs:481–487, 1092`, `VpnEngine.cs:888, 918`.
- **Test Evidence & Classification**: `NightBaselineEndpointTests.cs`: `Night05_Linux_EndpointHostname_IncludesIpv6InRuleset`, `Night05_Mac_EndpointHostname_IncludesIpv6InRuleset`; `CommittedFirewallConfigTests.cs`: `ParseServerIps_ExtractsOutboundsAndWireguardPeers_BothV4AndV6`; `VpnEngineApplyStructuralChangeTests.cs`: `ApplyAsync_HotReloadSucceeds_CallsFirewallCapabilityOnceWithExactGeneratedAndIntent`, `StartupPipeline_ColdOrderingSourceGuard_Phase6SkipsLegacyCapability_AndCommitOccursAfterStartBeforeMonitors`. *Behavioral (2 witness cases + 2 controls) & Source Guard (no live cold startup)*.
- **RED Witness**: Run [33973908946](https://github.com/PavelLizunov/VPNRouter/actions/runs/33973908946) (commit `4dbb28df`): 2 true WG IPv6 peer cases failed (`2001:db8::8` missing), 2 controls passed.
- **GREEN Witness**: Run [33973667482](https://github.com/PavelLizunov/VPNRouter/actions/runs/33973667482) (commit `54d6080c`, receipt [5552732672](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552732672)).

### NIGHT-06 — Failover Stale Pool & Intent Generation / Await-Window Rollback Race (P1)
- **Criterion & Code**: Reset failover context/pool on Start/Apply; `Stop()` bumps generation and nulls `_failover`; AutoFailover aborts on superseded intent. `VpnEngine.cs:45–46, 362, 397–404, 594–643, 887, 911, 966–967, 1789–1806`, `AutoFailoverEngine.cs:52–54, 93–97, 207–212, 235–252`.
- **Test Evidence & Classification**: `NightBaselineFailoverTests.cs`: `Night06_PublicStop_InvalidatesPreviousFailoverPool` (identity fixture SHA256 `345e09c0...`); `NightFailoverRollbackTests.cs`: `HandleDeadConfigAsync_WhenRestartAwaits_AndCommittedIntentBumpsGenerationAndSetsSelectionC_RetainsSelectionC_AndDoesNotSaveStore`. *Behavioral (await-window rollback race; no cross-thread atomicity claim)*.
- **RED Witness**: Run [33974662105](https://github.com/PavelLizunov/VPNRouter/actions/runs/33974662105) (commit `7f21286d`): `Assert.Null` failed on old failover pool after Stop.
- **GREEN Witness**: Run [33974410438](https://github.com/PavelLizunov/VPNRouter/actions/runs/33974410438) (commit `489a529b`, receipt [5552819799](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552819799)).

### NIGHT-07 — TwoPhaseStart & Durable Typed Readiness (P1)
- **Criterion & Code**: Phase B ignores clean `startTask` completion and awaits typed `Connected`; immediate fault/cancel aborts; legacy strings ignored; durable typed subscription. `TwoPhaseStartCoordinator.cs:192–267`, `MainWindowViewModel.Connection.cs:86–115`, `MainWindowViewModel.cs:2760, 7098`.
- **Test Evidence & Classification**: `NightBaselineRegressionTests.cs`: `Night07_TwoPhaseStartCoordinator_CleanStartTask_AwaitsTypedConnected`; `MvmTwoPhaseStartTimerTests.cs`: `Started_ThenCleanStartCompletion_LaterConnectedSucceeds`; `NightDurableReadinessTests.cs`: `Stopped_LegacyStrings_CannotSetIsConnected_TypedCurrentConnected_SetsIsConnectedTrue` (uninitialized reflection fixture), `Subscription_And_Unsubscription_SourceGuard`. *Behavioral, Reflection Fixture & Source Guard*.
- **RED Witness**: Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431) (commit `0a876da6`): Expected `Connected`, Actual `StartTaskCompleted`.
- **GREEN Witness**: Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449) (commit `c217389b`).

### NIGHT-08 — SingBox Stop/Reload & Apply Success Truth (P1)
- **Criterion & Code**: `RestartCore` returns explicit bool; unconfirmed exact stop preserves handle/lease and sets `State = Failed`; `ApplyAsync` restores baseline on reload/restart failure. `SingBoxManager.Lifecycle.cs:349–490, 594–675`, `SingBoxManager.cs:297–336`, `VpnEngine.cs:904–922`.
- **Test Evidence & Classification**: `NightBaselineStopCharacterizationTests.cs`: `WindowsExactStop_WhenKillThrows_RetainsOwnedHandleAndLease` (fixture SHA256 `56c5af60...`); `SingBoxManagerRestartTunLockTests.cs`: `Stop_LinuxCapabilityMode_FailedExactStop_PreservesLockAndReportsFailed`; `VpnEngineApplyStructuralChangeTests.cs`: `ApplyAsync_SingBoxReloadFails_RestoresBaselineAndReturnsFalseWithoutAppliedStatus`. *Behavioral (mock handle/lease seam)*.
- **RED Witness**: Run [33972547099](https://github.com/PavelLizunov/VPNRouter/actions/runs/33972547099) (commit `9975c168`): `Assert.Same` failed (handle was nulled on unconfirmed stop).
- **GREEN Witness**: Run [33972290164](https://github.com/PavelLizunov/VPNRouter/actions/runs/33972290164) (commit `945866c4`, Windows 69 passed, 0 skipped, receipt [5552568409](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552568409)).

### NIGHT-09 — Smart Connect ServerHealthProbe Unbounded Concurrency (P2)
- **Criterion & Code**: Concurrency bounded to `MaxConcurrency = 8` worker pool; caller selection preserved; deadline marks uncompleted probes dead without throwing; caller cancel rethrows. `ServerHealthProbe.cs:33, 58–70, 79–130`.
- **Test Evidence & Classification**: `NightBaselineRegressionTests.cs`: `Night09_ServerHealthProbe_ProbeAllAsync_PeakConcurrencyBoundedToEight`; `ServerHealthProbeTests.cs`: `MaxConcurrency_ConstantIsEight`, `ProbeAllAsync_MoreThanEightCandidates_BlockingTcsObservesMaxEight_AllEventuallyProcessed_FastestTailChosen`. *Behavioral (mock probe delegates)*.
- **RED Witness**: Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431) (commit `0a876da6`): Expected `8`, Actual `20`.
- **GREEN Witness**: Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449) (commit `c217389b`).

### NIGHT-10 — UDP Probe Cancelled Receive Falls Through to Ok/Slow (P2)
- **Criterion & Code**: Awaits `ReceiveAsync` directly; rethrows `OperationCanceledException` when `ct.IsCancellationRequested`, preventing cancelled probe from returning Ok/Slow. `TcpTlsProbe.cs:404, 525, 542–545, 570, 585–605`.
- **Test Evidence & Classification**: `NightBaselineRegressionTests.cs`: `Night10_TcpTlsProbe_ProbeUdpAsync_CancelAfterSend_ThrowsOperationCanceledException`; `NightUdpCancellationTests.cs`: `CancelAfterReceivesProbeDatagram_ThrowsOperationCanceledException`, `SilentListener_PermitsInternalTimeout_ReturnsOkWithRemark`. *Behavioral (loopback socket)*.
- **RED Witness**: Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431) (commit `0a876da6`): OCE not thrown (fell through to Ok).
- **GREEN Witness**: Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449) (commit `c217389b`).

### NIGHT-11 — ConnStats Retains Stale Metrics on API Error / Session & Apply Reset (P2)
- **Criterion & Code**: `ConnectionsSnapshot.IsValid` flag; API error sets `IsValid = false`; UI checks validity and invokes `ClearStatsState()`; session identity prevents stale updates. `ISingBoxApi.cs:143–157`, `ClashSingBoxApi.cs:239, 549–655`, `MainWindowViewModel.ConnStats.cs:56–83, 103–193`.
- **Test Evidence & Classification**: `NightBaselineStatsTests.cs`: `Night11_PollConnStatsAsync_OnApiFailure_ClearsConnectionStatsTextAndResetsBaseline` (fixture SHA256 `6845913e...`); `NightConnStatsSessionTests.cs`: `ValidNonZero_Then_ValidZero_ClearsStaleRateAndEstablishesFreshBaseline`, `CounterRegression_ResetsBaselineAndClearsText`. *Behavioral (mock API double)*.
- **RED Witness**: Run [33970863633](https://github.com/PavelLizunov/VPNRouter/actions/runs/33970863633) (commit `7c8fe020`): Expected `""`, Actual `"oldtext"`.
- **GREEN Witness**: Run [33970601295](https://github.com/PavelLizunov/VPNRouter/actions/runs/33970601295) (commit `b322d1fc`, receipt [5552381401](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552381401)).

### NIGHT-12 — Clash WebSocket Telemetry Auth Secret Omission & Exception Sanitizer (P2)
- **Criterion & Code**: Passes `secret: settings.SingBox.ClashApiSecret` to `ClashLogStream`; `LogStreamFailure` sanitizes logs without leaking exception/URI/token; `RedactLogsUri`. `VpnEngine.cs:1201–1206`, `ClashLogStream.cs:97–108, 156–159`.
- **Test Evidence & Classification**: `NightBaselineTelemetryWiringTests.cs`: `VpnEngine_TryStartConnectionHealthStream_PassesClashApiSecret` (wiring fixture SHA256 `3b9a9c0d...`); `ClashLogStreamTests.cs`: `TryStartConnectionHealthStream_PassesClashApiSecret_CommentsStripped`, `LogStreamFailure_NestedExceptionContainingTokenOrUri_NeverLeaksIntoRenderPropertiesOrException`. *Source Wiring & Synthetic Logging (no live handshake)*.
- **RED Witness**: Run [33971648760](https://github.com/PavelLizunov/VPNRouter/actions/runs/33971648760) (commit `0d757660`): named secret argument absent in `VpnEngine`.
- **GREEN Witness**: Run [33971405494](https://github.com/PavelLizunov/VPNRouter/actions/runs/33971405494) (commit `0072c571`, receipt [5552469312](https://github.com/PavelLizunov/VPNRouter/pull/240#issuecomment-5552469312)).

---

## 5. Unresolved Baseline Follow-ups & Release Gate Status

The following registered baseline defects remain **OPEN P1** in `plans/OPEN-DEFECTS.md` and are excluded from the 12-defect repair scope:
- **NIGHT-FOLLOWUP-01**: `SingBoxManager.Lifecycle.cs:88, 587, 905`: catch ~88 and ~590 unconditionally invoke `ReleaseTunOwnership` after `Started` throws with a retained live handle.
- **NIGHT-FOLLOWUP-02**: `StartupPipeline.cs:699–716`: SafeMode split routing mismatch (forces `activeProfile` to `"FullTunnel"`, but `ConfigGenerator` reads `settings.App.RoutingMode` `"split"`, emitting `route.final = "direct"`; naive `profile.Name == "FullTunnel"` fix rejected as unsafe due to arbitrary profile names).

**Release Semantics**: Unchanged. Defect ledger in `plans/OPEN-DEFECTS.md` records only the 12 night audit defects resolved in PR #240 (unreleased; target version TBD); 2 P1 follow-ups (`NIGHT-FOLLOWUP-01` and `NIGHT-FOLLOWUP-02`) remain **OPEN**. Implementation and mock/seam CI verification are complete for all 12 night audit defects within approved non-live constraints; product code last tested at `489a529b` is unaffected by docs-only updates; full live solution acceptance, native TUN testing, and production cut remain subject to follow-up triage and explicit owner authorization.
