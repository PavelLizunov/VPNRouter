# Night Audit RED-GREEN Verification Matrix (2026-09-05)

**Baseline SHA**: `b7ce0e4f` | **Fixed SHA**: `23cc4e52` / `c217389b` | **Task Branch**: `dsh/fix-night-audit-gemini-2026-09-04` (PR #240) | **Isolated Baseline Branch**: `dsh/fix-night-audit-red-verification-2026-09-05` (Commit `0a876da6`)
**Policy Rule**: Pre-fix RED states for NIGHT-02, 03, 07, 09, 10 verified on isolated baseline run 33969048431; remaining 7 defects explicitly labeled **NOT EXECUTED** (no retro-fabrication). All post-fix GREEN states mapped to actual CI runs (witness run 33968752449). Whole 12 closure remains pending; no promise of full solution/native; isolated baseline branch, no PR, no workflow edit, no release.

---

## 1. Review Governance & Rejected Claims Log

The lead rejected the initial review and previous document assertions on the following technical and policy grounds:
1. **Hallucinated Test Names**: The initial review hallucinated non-existent test names (e.g. `StartupPipeline_SafeMode_ArmsCommittedFirewallFullTunnel`). All test references must strictly name actual codebase files.
2. **`ClashLogStream` Constructor Signature**: The `secret` constructor parameter (`string? secret = null`) **already existed on baseline `b7ce0e4f`** (`VPNRouter.Core/Services/ClashLogStream.cs`). Baseline `VpnEngine.cs` simply omitted the argument during instantiation. NIGHT-12 added argument passing in `VpnEngine` and `LogStreamFailure` in `ClashLogStream`.
3. **`SingBoxState.Failed` Baseline Existence**: `SingBoxState.Failed` **already existed on baseline `b7ce0e4f`** (`SingBoxManager.cs`). Only `_exactStopUnconfirmed` and boolean `ReloadConfigJsonWithResult` were absent.
4. **Unexecuted Compilation Claims**: Unexecuted cherry-picks cannot claim verified compilation ("Compiles cleanly"). The matrix now assesses **STATIC baseline API compatibility (not executed)**.
5. **Reflection `Assert.Null` is NOT a RED Bug Witness**: An assertion like `Assert.Null(typeof(...).GetMethod(...))` **passes** on baseline; because it does not fail, it cannot serve as a RED bug witness.
6. **Missing APIs Do Not Mandate Source-Only**: Behavioral tests do not strictly require new post-fix APIs; baseline-compatible tests can adapt to baseline interfaces to witness the bug behaviorally.
7. **Fake/Mock Tests Do Not Require Live VPN**: Fake and mock harnesses (e.g. synthetic event lambdas, fake HTTP handlers, mock CLI runners, loopback sockets) run in-memory and explicitly do **not** require a live VPN or live OS infrastructure.
8. **SafeMode Finding Rejected as Duplicate**: Proposed SafeMode route findings were rejected as a duplicate of registered defect `NIGHT-FOLLOWUP-02` (in `StartupPipeline.cs:699–716`, `SafeMode.Enabled` forces `activeProfile` to `"FullTunnel"`, but `ConfigGenerator.Generate` reads `settings.App.RoutingMode`, emitting `route.final = "direct"` instead of full tunnel under split settings; attempting a `profile.Name == "FullTunnel"` fix is unsafe due to arbitrary profile names).
9. **Rejection of `TgProxyPid` Throws Finding**: Lead source verification rejected the proposed `TgProxyPid` throws finding. Inspection of `VPNRouter.Core/Services/ProcessRunner.cs:213` confirms that `Pid` is an in-memory cached auto-property (`public int Pid { get; private set; }`), NOT an active `Process.Id` OS query. A fabricated custom test double (`fakePidThrow`) does not demonstrate a product bug.
10. **SafeMode Finding Remains Deferred**: SafeMode route semantic mismatch remains tracked as duplicate `NIGHT-FOLLOWUP-02` (`StartupPipeline.cs:699–716`) and deferred outside the 12 repair scope.

---

## 2. CI Execution & Dispatch Architecture

- **Existing CI Harness**: `.github/workflows/test.yml` defines `workflow_dispatch` without inputs, natively supporting execution against arbitrary branches/refs without modifying workflow YAML.
  - *Ubuntu test command*: `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff" --logger "trx;LogFileName=test-results.trx" --logger "console;verbosity=normal"`
  - *Windows characterization*: `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~Characterization|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"`
- **Branch & Worktree Policy**: Pushing branches or modifying worktrees requires task policy authorization. This assignment has **NO authority to create worktrees, push branches, or open PRs**.
- **Dynamic Compilation Assessment**: Dynamic in-tree Roslyn compilation of baseline fixtures is rejected as fragile.
- **Isolated Baseline Verification Execution**: The lead observed baseline verification executed on isolated branch `dsh/fix-night-audit-red-verification-2026-09-05` (parent `b7ce0e4f` plus ONLY same `NightBaselineRegressionTests.cs` file SHA256 `4905d9e6be76d3192865dd0b9726003c7171a6c34c331f1b2d142702d207cdd3` at exact commit `0a876da62ec3a753ebef393f3d6d066fd6b32c68`). It built successfully with 5 test assertions failing and Go/Windows characterization green (CI run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431)). Fixed GREEN verified on commit `c217389b80af91b697a9f62f841a101000b6705e` (CI run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449), all 4 green, explicitly 5 passed). No PR, no workflow edit, no release.
- **Destructive Execution Invariant**: Explicitly **NO real `TgProxyKillAll` or OS process sweeps** even on baseline; tests must use mock listeners, fake sinks, or source inspection. Mocks and loopback sockets do not require live VPN connections.

---

## 3. Twelve-Defect RED/GREEN Verification Matrix

| Defect ID & Title | Fixed Commit & Green CI Run | Regression Test File(s) | Pre-Fix RED (Baseline `b7ce0e4f`) | STATIC Baseline Compatibility Assessment | Baseline RED Feasibility & Mode |
|---|---|---|---|---|---|
| **NIGHT-01** (P1): TgProxy port-only kill terminates foreign process | `ff4bf3d4`, `4d029a54` (Run 33947035219) | `TgProxyOwnershipCharacterizationTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: Relies on refactored `TgProxyManager` lifecycle and ownership models from `ff4bf3d4` | **Feasible via Fake Runner / Sink**: Process mock can adapt to baseline API; unsafe on OS if calling live PID kill |
| **NIGHT-02** (P1): Custom WG detour rewritten to dns-direct | `ff4bf3d4`, `4d029a54` (Run 33947035219); Witness: `c217389b` (Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449), 5 passed) | `NightDnsPrivacyRegressionTests.cs`, `NightBaselineRegressionTests.cs` | **OBSERVED RED** (Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431), commit `0a876da6`): Expected `"wg"`, Actual `"dns-direct"` (`/tmp/vpnrouter-baseline-red-failures.log:1368–1376`) | **Compatible**: Executed and verified on baseline `0a876da6` | **Verified RED & Safe**: In-memory JSON unit test fails (outputs `dns-direct`); zero network or VPN required |
| **NIGHT-03** (P1): StrictDns overridden by smart-DNS rule | `ff4bf3d4`, `4d029a54` (Run 33947035219); Witness: `c217389b` (Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449), 5 passed) | `NightDnsPrivacyRegressionTests.cs`, `NightBaselineRegressionTests.cs` | **OBSERVED RED** (Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431), commit `0a876da6`): Expected `"vpn-dns"`, Actual `"local-dns"` (`/tmp/vpnrouter-baseline-red-failures.log:1397–1405`) | **Compatible**: Executed and verified on baseline `0a876da6` | **Verified RED & Safe**: In-memory JSON unit test fails (matches local-dns rule); zero network or VPN required |
| **NIGHT-04** (P1): Failed nft/pf delete removes recovery marker | `4ce2fc30` (Run 33948404811) | `LinuxFirewallManagerTests.cs`, `MacFirewallManagerTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: Test relies on internal runner seams and JSON parsing signatures added in `4ce2fc30` | **Feasible via Mock Runner**: Baseline-compatible test can adapt to baseline runner seams; no live firewall or VPN needed |
| **NIGHT-05** (P1): Unix firewall stale config, no Apply refresh | `b7406456`, `8ee89105` (Run 33954240530) | `CommittedFirewallConfigTests.cs`, `LinuxFirewallManagerTests.cs`, `MacFirewallManagerTests.cs`, `VpnEngineApplyStructuralChangeTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: `ICommittedFirewallConfig` interface, `UpdateCommittedConfig`, and Apply wiring absent | **Feasible via Adapted Test**: Baseline-compatible tests can adapt to baseline firewall API to demonstrate lack of refresh; does not mandate source-only |
| **NIGHT-06** (P1): Failover stale pool & await-window rollback | `da3c21e7` (Run 33956946168), `6b140a69` (Run 33964403103) | `NightFailoverIntentTests.cs`, `NightFailoverRollbackTests.cs`, `VpnEngineApplyStructuralChangeTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: `ResetFailoverContext`, `_failoverGeneration`, and rollback guards absent | **Feasible via Adapted Test**: Baseline-compatible test can adapt using baseline failover/Apply invocations; does not mandate source-only |
| **NIGHT-07** (P1): StartTask clean completion cancels Phase B | `5ecbe4d2` (Run 33955634699), `23cc4e52` (Run 33967483426); Witness: `c217389b` (Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449), 5 passed) | `MvmTwoPhaseStartTimerTests.cs`, `NightTypedReadinessTests.cs`, `NightBaselineRegressionTests.cs` | **OBSERVED RED** (Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431), commit `0a876da6`): Expected `Connected`, Actual `StartTaskCompleted` (`/tmp/vpnrouter-baseline-red-failures.log:1360–1367`) | **Compatible**: Executed and verified on baseline `0a876da6` | **Verified RED via Fake Coordinator**: Decoupled synthetic event lambdas fail; zero live VPN or process required |
| **NIGHT-08** (P1): Apply commits on failed Stop; unconfirmed stop | `1ca812f4` (Run 33951772995), `0086589d` (Run 33963417688) | `SingBoxManagerRestartTunLockTests.cs`, `NightWindowsStopCharacterizationTests.cs`, `VpnEngineApplyStructuralChangeTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: `ReloadConfigJsonWithResult` returning `bool` and `_exactStopUnconfirmed` absent (`SingBoxState.Failed` exists) | **Feasible via Adapted Test**: Baseline-compatible test can adapt to baseline `ReloadConfigJson` / process runner seams; does not mandate source-only |
| **NIGHT-09** (P2): ServerHealthProbe unbounded peak concurrency | `530ca9e7` (Run 33957757306); Witness: `c217389b` (Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449), 5 passed) | `ServerHealthProbeTests.cs`, `NightBaselineRegressionTests.cs` | **OBSERVED RED** (Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431), commit `0a876da6`): Expected `8`, Actual `20` (peak 8/20) (`/tmp/vpnrouter-baseline-red-failures.log:1381–1388`) | **Compatible**: Executed and verified on baseline `0a876da6` | **Verified RED & Safe**: Behavioral unit test fails (peak concurrency exceeds bounded limit); mock probe, zero network/VPN |
| **NIGHT-10** (P2): Cancelled UDP receive falls through to Ok | `530ca9e7` (Run 33957757306); Witness: `c217389b` (Run [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449), 5 passed) | `NightUdpCancellationTests.cs`, `NightBaselineRegressionTests.cs` | **OBSERVED RED** (Run [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431), commit `0a876da6`): `Assert.ThrowsAny<OperationCanceledException>()` threw no exception (`/tmp/vpnrouter-baseline-red-failures.log:1389–1396`) | **Compatible**: Executed and verified on baseline `0a876da6` | **Verified RED & Safe**: Loopback socket (`127.0.0.1`) test fails (OCE not rethrown); zero external network or live VPN |
| **NIGHT-11** (P2): ConnStats retains stale metrics on API error | `7c980af4` (Run 33960085565), `9a292979` (Run 33965171685) | `NightConnStatsSessionTests.cs` | **NOT EXECUTED** | **Statically Compatible**: MVM uninitialized object reflection with `FakeClashHttpHandler` | **Feasible & Safe**: Behavioral test fails (stale download/upload rates retained on 500 error); zero live VPN or GUI |
| **NIGHT-12** (P2): ClashLogStream secret omitted in VpnEngine; exception token leak | `09db6ec5` (Run 33958360119), `83a1bd48` (Run 33961291200) | `ClashLogStreamTests.cs` | **NOT EXECUTED** | **Statically Incompatible with post-fix tests**: Ctor with `secret` already existed on baseline (`VpnEngine` omitted argument); `LogStreamFailure` helper absent | **Feasible via Adapted Test**: Baseline-compatible test can verify `VpnEngine` ctor call or instantiate `ClashLogStream`; does not mandate source-only |

---

## 4. Baseline Compatibility & RED Witness Adaptation Strategy

### Accurate Baseline Facts
- **`ClashLogStream`**: The constructor `public ClashLogStream(string clashBaseUrl, ConnectionHealthState state, Func<IReadOnlySet<string>?>? proxyEndpoints = null, ILogger? logger = null, string? secret = null)` existed on baseline `b7ce0e4f`. `VpnEngine.cs` simply omitted the parameter when constructing `_connHealthStream`. Commit `09db6ec5` added `secret: settings.SingBox.ClashApiSecret`, and `83a1bd48` added `LogStreamFailure`.
- **`SingBoxState.Failed`**: Fully present in `SingBoxManager.cs` on baseline `b7ce0e4f` (`public enum SingBoxState { Stopped, Starting, Running, Restarting, Failed }`). Only `_exactStopUnconfirmed` and boolean `ReloadConfigJsonWithResult` were added during the fix.

### Designing Valid RED Bug Witnesses (Avoiding False Passing Assertions)
1. **Reflection Absence Checks Are Not RED Bug Witnesses**: Asserting `Assert.Null(typeof(SingBoxManager).GetMethod("ReloadConfigJsonWithResult"))` **passes** on baseline. A genuine RED test must fail on buggy code and pass on fixed code.
2. **Adapting Baseline-Compatible Tests**: Where post-fix regression tests invoke new APIs (such as `ReloadConfigJsonWithResult`), the baseline verification suite must adapt tests to target baseline APIs directly (e.g. checking whether `ApplyAsync` proceeds despite a failed reload), proving behavioral failure without compile breaks.
3. **No Live Infrastructure Required**: Tests utilizing mocks, fakes, synthetic delegates, and loopback sockets do not require live VPN servers, live network adapters, or live OS process sweeps.

---

## 5. Observed Baseline RED/GREEN Verification Checkpoint (2026-09-05)

- **Baseline RED Verification**:
  - **CI Run**: [33969048431](https://github.com/PavelLizunov/VPNRouter/actions/runs/33969048431)
  - **Exact Commit**: `0a876da62ec3a753ebef393f3d6d066fd6b32c68` on isolated baseline branch `dsh/fix-night-audit-red-verification-2026-09-05`
  - **Composition**: Parent `b7ce0e4f` plus ONLY same `NightBaselineRegressionTests.cs` (SHA256: `4905d9e6be76d3192865dd0b9726003c7171a6c34c331f1b2d142702d207cdd3`)
  - **Build & Suite Outcome**: Built successfully; Go and Windows characterization suites green; 5 baseline tests failed assertions (`/tmp/vpnrouter-baseline-red-failures.log:1360–1405`):
    - **NIGHT-02**: Expected `"wg"`, Actual `"dns-direct"` (pos 0)
    - **NIGHT-03**: Expected `"vpn-dns"`, Actual `"local-dns"` (pos 0)
    - **NIGHT-07**: Expected `Connected`, Actual `StartTaskCompleted`
    - **NIGHT-09**: Expected `8`, Actual `20` (peak concurrency 8 vs 20)
    - **NIGHT-10**: `Assert.ThrowsAny<OperationCanceledException>()` — no exception was thrown
- **Fixed GREEN Witness**:
  - **CI Run**: [33968752449](https://github.com/PavelLizunov/VPNRouter/actions/runs/33968752449)
  - **Commit**: `c217389b80af91b697a9f62f841a101000b6705e`
  - **Suite Outcome**: All 4 CI checks green; explicitly all 5 baseline tests passed (`/tmp/vpnrouter-baseline-witness-green.log:4246–4250`).
- **Unexecuted Defects**: Pre-fix RED for remaining 7 defects (NIGHT-01, 04, 05, 06, 08, 11, 12) remains **NOT EXECUTED**.
- **Governance & Process Invariants**:
  - Whole 12 defect closure remains **PENDING**; no promise of full solution or native testing.
  - Isolated baseline branch `dsh/fix-night-audit-red-verification-2026-09-05`; no PR, no workflow edit, no release.
  - Lead source verification rejects proposed `TgProxyPid` throws finding: `VPNRouter.Core/Services/ProcessRunner.cs:213` is a cached auto-property (`public int Pid { get; private set; }`), NOT an OS `Process.Id` query; fabricated `fakePidThrow` test double does not demonstrate a product bug.
  - SafeMode finding duplicate `NIGHT-FOLLOWUP-02` remains deferred.
