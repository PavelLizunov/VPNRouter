# Night Audit RED-GREEN Verification Matrix (2026-09-05)

**Baseline SHA**: `b7ce0e4f` | **Fixed SHA**: `23cc4e52` | **Task Branch**: `dsh/fix-night-audit-gemini-2026-09-04` (PR #240)  
**Policy Rule**: All pre-fix RED states explicitly labeled **NOT EXECUTED** (no retro-fabrication). All post-fix GREEN states mapped to actual CI runs. New baseline-compatible test fixtures are being authored in separate tests.

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

---

## 2. CI Execution & Dispatch Architecture

- **Existing CI Harness**: `.github/workflows/test.yml` defines `workflow_dispatch` without inputs, natively supporting execution against arbitrary branches/refs without modifying workflow YAML.
  - *Ubuntu test command*: `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff" --logger "trx;LogFileName=test-results.trx" --logger "console;verbosity=normal"`
  - *Windows characterization*: `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~Characterization|FullyQualifiedName~PostShipVerifierContractTests|FullyQualifiedName~BratVerifierContractTests"`
- **Branch & Worktree Policy**: Pushing branches or modifying worktrees requires task policy authorization. This assignment has **NO authority to create worktrees, push branches, or open PRs**.
- **Dynamic Compilation Assessment**: Dynamic in-tree Roslyn compilation of baseline fixtures is rejected as fragile.
- **Recommended Verification Approach**: When authorized, create a temporary verification branch based on `b7ce0e4f` with separate baseline-compatible test fixtures asserting expected RED outcomes, triggered via `workflow_dispatch`.
- **Destructive Execution Invariant**: Explicitly **NO real `TgProxyKillAll` or OS process sweeps** even on baseline; tests must use mock listeners, fake sinks, or source inspection. Mocks and loopback sockets do not require live VPN connections.

---

## 3. Twelve-Defect RED/GREEN Verification Matrix

| Defect ID & Title | Fixed Commit & Green CI Run | Regression Test File(s) | Pre-Fix RED (Baseline `b7ce0e4f`) | STATIC Baseline Compatibility Assessment (Not Executed) | Baseline RED Feasibility & Mode |
|---|---|---|---|---|---|
| **NIGHT-01** (P1): TgProxy port-only kill terminates foreign process | `ff4bf3d4`, `4d029a54` (Run 33947035219) | `TgProxyOwnershipCharacterizationTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: Relies on refactored `TgProxyManager` lifecycle and ownership models from `ff4bf3d4` | **Feasible via Fake Runner / Sink**: Process mock can adapt to baseline API; unsafe on OS if calling live PID kill |
| **NIGHT-02** (P1): Custom WG detour rewritten to dns-direct | `ff4bf3d4`, `4d029a54` (Run 33947035219) | `NightDnsPrivacyRegressionTests.cs` | **NOT EXECUTED** | **Statically Compatible**: `CustomConfigInjector.Inject` JSON transformation API unchanged | **Feasible & Safe**: In-memory JSON unit test fails (outputs `dns-direct`); zero network or VPN required |
| **NIGHT-03** (P1): StrictDns overridden by smart-DNS rule | `ff4bf3d4`, `4d029a54` (Run 33947035219) | `NightDnsPrivacyRegressionTests.cs` | **NOT EXECUTED** | **Statically Compatible**: `ConfigGenerator.Dns` rule generation methods unchanged | **Feasible & Safe**: In-memory JSON unit test fails (matches local-dns rule); zero network or VPN required |
| **NIGHT-04** (P1): Failed nft/pf delete removes recovery marker | `4ce2fc30` (Run 33948404811) | `LinuxFirewallManagerTests.cs`, `MacFirewallManagerTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: Test relies on internal runner seams and JSON parsing signatures added in `4ce2fc30` | **Feasible via Mock Runner**: Baseline-compatible test can adapt to baseline runner seams; no live firewall or VPN needed |
| **NIGHT-05** (P1): Unix firewall stale config, no Apply refresh | `b7406456`, `8ee89105` (Run 33954240530) | `CommittedFirewallConfigTests.cs`, `LinuxFirewallManagerTests.cs`, `MacFirewallManagerTests.cs`, `VpnEngineApplyStructuralChangeTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: `ICommittedFirewallConfig` interface, `UpdateCommittedConfig`, and Apply wiring absent | **Feasible via Adapted Test**: Baseline-compatible tests can adapt to baseline firewall API to demonstrate lack of refresh; does not mandate source-only |
| **NIGHT-06** (P1): Failover stale pool & await-window rollback | `da3c21e7` (Run 33956946168), `6b140a69` (Run 33964403103) | `NightFailoverIntentTests.cs`, `NightFailoverRollbackTests.cs`, `VpnEngineApplyStructuralChangeTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: `ResetFailoverContext`, `_failoverGeneration`, and rollback guards absent | **Feasible via Adapted Test**: Baseline-compatible test can adapt using baseline failover/Apply invocations; does not mandate source-only |
| **NIGHT-07** (P1): StartTask clean completion cancels Phase B | `5ecbe4d2` (Run 33955634699), `e3240790`, `23cc4e52` | `MvmTwoPhaseStartTimerTests.cs`, `NightTypedReadinessTests.cs`, `NightDurableReadinessTests.cs` | **NOT EXECUTED** | **Statically Mixed**: Coordinator seams compatible; typed `Connected` event and durable subscriber absent | **Feasible via Fake Coordinator**: Decoupled synthetic event lambdas fail; zero live VPN or process required |
| **NIGHT-08** (P1): Apply commits on failed Stop; unconfirmed stop | `1ca812f4` (Run 33951772995), `0086589d` (Run 33963417688) | `SingBoxManagerRestartTunLockTests.cs`, `NightWindowsStopCharacterizationTests.cs`, `VpnEngineApplyStructuralChangeTests.cs` | **NOT EXECUTED** | **Statically Incompatible**: `ReloadConfigJsonWithResult` returning `bool` and `_exactStopUnconfirmed` absent (`SingBoxState.Failed` exists) | **Feasible via Adapted Test**: Baseline-compatible test can adapt to baseline `ReloadConfigJson` / process runner seams; does not mandate source-only |
| **NIGHT-09** (P2): ServerHealthProbe unbounded peak concurrency | `530ca9e7` (Run 33957757306) | `ServerHealthProbeTests.cs` | **NOT EXECUTED** | **Statically Compatible**: `ServerHealthProbe.ProbeAllAsync` signature and `probeOverride` seam unchanged | **Feasible & Safe**: Behavioral unit test fails (peak concurrency exceeds bounded limit); mock probe, zero network/VPN |
| **NIGHT-10** (P2): Cancelled UDP receive falls through to Ok | `530ca9e7` (Run 33957757306) | `NightUdpCancellationTests.cs` | **NOT EXECUTED** | **Statically Compatible**: `TcpTlsProbe.ProbeUdpAsync` signature unchanged | **Feasible & Safe**: Loopback socket (`127.0.0.1`) test fails (OCE not rethrown); zero external network or live VPN |
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
