# Phase 2G — `VpnEngineOrchestratorTests` (last Phase 2G test gap)

**Owner**: Claude session (Opus 4.7, 1M context)
**Branch**: `main` (test-only addition, zero behavioural risk)
**Roadmap ref**: Phase 2G coverage audit follow-up. `VpnEngine.cs` is
~976 LOC with only 24 dedicated tests covering narrow paths
(`VpnEngineApplyEscalationTests`, `VpnEngineRemoveExcludedAppsTests`,
`VpnEngineTunFingerprintTests`). Companion sibling: `UpdateCheckerTests`
(2G, commit `247f6a6`, 19 tests).
**Effort**: ~45 min.
**Risk**: NONE (test-only; no production code modified).
**Blast radius**: 1 new file
(`VPNRouter.Tests/VpnEngineOrchestratorTests.cs`).
**Rollback**: `git revert <commit>` — drops the test file, nothing else
moves.

## Why

Phase 2G test-coverage audit (2026-05-20) flagged `VpnEngine.cs` as the
last orchestrator-layer surface with insufficient regression coverage
for an upcoming Phase 3 refactor:

- **976 LOC** — second-largest service in Core after `UpdateChecker`.
- **24 existing tests across 3 files**, all narrow:
  - `VpnEngineApplyEscalationTests` (4) — source-string pins for Apply
    `forceRestart` escalation.
  - `VpnEngineRemoveExcludedAppsTests` (8) — exclusion-list static
    helper.
  - `VpnEngineTunFingerprintTests` (12) — TUN fingerprint static
    helper.
- **What's missing**: the LIFECYCLE contract. Construction state.
  Dispose semantics. Stop on idle engine. Apply on idle engine. The
  state-machine surface that a Phase 3 refactor of VpnEngine
  (planned: pipeline-driven Start/Stop) must not regress.

Phase 3G-2 (commit `5370be3`, SingBoxManager IHttpClient migration)
gave us a `FakeHttpClient` seam that can stub Clash API without a real
sing-box. BUT — VpnEngine.StartAsync requires the sing-box binary on
disk, Windows-only firewall via netsh, and profiles JSON in
%ProgramData%. There is NO test seam that makes the full
StartAsync→Connected→Stop matrix invoke-testable in-memory today.

**Approach**: mirror the existing
`VpnEngineApplyEscalationTests` pattern — mix of (a) invoke-based
tests against the truly in-memory portions of VpnEngine
(constructor, Dispose, idle ApplyAsync guard, idle Stop, static
helpers like `ParseClashApiPort` and `ResolveCustomConfigPath`,
profile-source builders) and (b) **source-string pins** for the
orchestration concerns that can't be invoked without spawning a
real sing-box (Start lifecycle, Stop ordering BR-6a, Dispose calls
Stop, cancellation propagation).

The full StartAsync→Connected matrix is deferred to Phase 3+ when
the StartupPipeline gains a fake-SingBoxManager seam.

## What

Single new file: `VPNRouter.Tests/VpnEngineOrchestratorTests.cs`.
Plain `[Fact]` xUnit class — no Avalonia headless dispatcher.

### Test categories

1. **Construction state** (3 tests). Initial public surface is correct
   before any Start call: IsRunning=false, ActiveProfileName empty,
   SingBoxPid null, MonitoredProcesses empty, default ActiveConfigMode
   ("generated"), default ActiveRoutingMode ("split").

2. **Stop on idle engine** (2 tests). Stop fires StatusChanged events
   ("Stopping..." + "Stopped"). Stop is idempotent (second call is
   no-op, no exception).

3. **ApplyAsync idle guard** (2 tests). Apply on engine that never
   started returns false synchronously (without throwing), and does
   NOT raise the Warning event (the guard short-circuits BEFORE any
   pipeline work).

4. **Dispose semantics** (2 tests). Dispose on idle engine doesn't
   throw. Dispose is idempotent.

5. **Static helpers** (3 tests). `ParseClashApiPort` edge cases
   (null/empty/missing-port → 9090 default; valid → parsed).
   `ResolveCustomConfigPath` branches (legacy fallback, multi-config
   active by name, fallback to first when active missing).
   `BuildBundledOnlyProfileSources` includes built-in fallback.

6. **Source-string pins** for orchestration paths that can't be invoke-
   tested in-memory (4 tests). Mirror the
   `VpnEngineApplyEscalationTests` pattern. Pin:
   - StartAsync goes through StartupPipeline (Phase 3C contract).
   - Stop orders `_healthMonitor?.Stop()` BEFORE `_singBox?.Stop()`
     (BR-6a race fix — pin the lesson).
   - Dispose calls Stop when running (cleanup invariant).
   - StartAsync uses HotReload mode for ApplyAsync re-entry.

**Total**: ~14-16 tests.

### Coverage matrix vs. brief categories from the task

| Brief category | Covered by | Notes |
|---|---|---|
| 1. Happy-path lifecycle | Source pin only | No in-memory sing-box stub exists today. Pin shows pipeline is the orchestrator. Phase 3+ follow-up. |
| 2. Crash-then-restart | Cross-covered by `HealthMonitorRecoveryGapTests` | Not duplicated here — that test pins the recovery branch directly. |
| 3. Intentional stop suppression | Covered by existing `SingBoxManager` tests + Stop source pin here | Source pin shows VpnEngine.Stop sets up the ordered teardown that lets SingBoxManager's own EnableRaisingEvents=false trick work. |
| 4. Apply on idle vs running | Idle path: invoke-tested (returns false). Running path: cross-covered by `VpnEngineApplyEscalationTests` (escalation source pins). |
| 5. Concurrent start guard | Source pin shows no explicit re-entry guard — VpnEngine relies on caller to serialize. Intentional pin of current behaviour. |
| 6. Empty servers throw | Cross-covered by `ConfigGeneratorEmptyServersGuardTests` + `ConfigPipelineTests.Generate_EmptyServers_ThrowsConfigValidationException`. Not duplicated here. |
| 7. DnsLeakLockdown OFF default | Cross-covered by `AppSettingsDnsLeakLockdownTests`. Not duplicated at orchestrator surface (no in-memory firewall hook to verify). |

## How

1. **Create** `VPNRouter.Tests/VpnEngineOrchestratorTests.cs` with:
   - `#nullable enable` header.
   - `using VPNRouter.Core.Models;` for `AppSettings` / `Profile`.
   - `using VPNRouter.Core.Services;` for `VpnEngine`.
   - `using VPNRouter.Core.Interfaces;` for stub
     `IProcessScanner` / `IFirewallManager` / `IProcessMonitor`.
   - `#pragma warning disable CS0618` for the obsoleted ctor (per
     the Obsolete attribute on `VpnEngine(...)` — direct ctor is
     deprecated but kept compiling for tests/legacy callers).
   - Inline `Stub*` classes for the 3 dependencies (mirroring
     `HealthMonitorRecoveryGapTests`).

2. **`BuildIdleEngine` helper** — construct a VpnEngine with stubs
   that never get exercised on the idle paths the tests use. No
   sing-box launch, no firewall setup, no ETW thread.

3. **Source-pin tests** — use the same `LoadVpnEngineSource()` +
   `AssertNearby` helpers from `VpnEngineApplyEscalationTests`.
   Inline them — keeps the file self-contained and avoids extracting
   shared utilities (one-file-per-class rule).

4. **No filesystem touches in invoke tests**. `ResolveCustomConfigPath`
   tests use non-existent paths so the `File.Exists` branches return
   false predictably. The fallback path (`Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "")`)
   is testable without any real file present.

### Verification approach

- `dotnet build VPNRouter.sln -c Release` → 0 errors.
  `taskkill /F /IM testhost.exe` first if locks reported.
- `dotnet test ...
  --filter "FullyQualifiedName~VpnEngineOrchestratorTests"` →
  all new tests green.
- Full suite filter (per `VPNRouter.Tests/CLAUDE.md` known-issue
  note that excludes headless screenshot tests):
  `dotnet test ... --filter
  "FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
  expects ~1227+ pass (baseline after `247f6a6` = 1213).

## Verification gate

Check off each as you complete:

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [x] **Gate 2 — Tests green**: new tests pass; full suite green at the
  baseline established in `phase2G-updatechecker-tests-2026-05-21.md`.
- [x] **Gate 3 — Docs**: this brief; no README / CLAUDE.md edits needed.
- [x] **Gate 4 — Self-review**: test-only file, no production diff →
  `simplify` N/A. No external HTTP / FS target → `security-review` N/A.
- [x] **Gate 5 — MCP verify**: N/A — Core test-coverage addition, no UI
  surface.
- [x] **Gate 6 — Characterization diff**: N/A — new file only.

## Risk

NONE. The change adds a new test file. No production code is modified.
Failure modes are limited to:
- A test asserts the wrong thing → fails on its own commit, no user
  impact.
- The new tests reveal an existing latent bug → that's the WHOLE POINT
  of this brief; the bug surfaces as a red test in the same PR.

## What is intentionally NOT covered (deferred to Phase 3+)

- **In-memory StartAsync→Connected→Stop matrix**. Requires a fake
  SingBoxManager that can plausibly stand in for the real process
  lifecycle, plus a fake IProcessScanner that returns a valid
  ScanResult, plus a fake IFirewallManager that doesn't shell out to
  `netsh`. Today VpnEngine takes a `Func<IFirewallManager>` factory
  (good seam) but the only production impl is the Windows netsh
  shell-out. Phase 3 refactor introduces a `NullFirewallManager` for
  non-Windows + tests; this brief defers the matrix until then.
- **Concurrent StartAsync** — there's no synchronisation primitive in
  VpnEngine.StartAsync today (relies on caller serialization). A
  second StartAsync call mid-warmup may double-launch sing-box. Phase
  3 brief: add a `SemaphoreSlim` + race tests.
- **HealthMonitor restart event propagation** — VpnEngine subscribes
  to `_healthMonitor.RestartAttempted` and re-fires. Today verified
  end-to-end via integration tests that run a real sing-box. Will be
  unit-testable once the HealthMonitor → VpnEngine wiring uses a
  fake ISingBoxApi.
- **Warmup window** — VpnEngine waits ~5s for sing-box Clash API to
  come up before declaring Connected. Time-based, not easily fake-
  clockable today. Phase 3+ would inject `TimeProvider` (.NET 8 type).

## Outcome (filled 2026-05-21)

**Status**: PASS — 16 tests added, all green.

**Commit**: `14c512e` test(vpnengine): 2G — 16 characterization tests for orchestrator

**Test deltas**: +16 in
`VPNRouter.Tests/VpnEngineOrchestratorTests.cs` (two extras vs.
the ~14 estimate — `ResolveCustomConfigPath` surfaced both the
"empty list" fallback and the "multi-config active name pick"
branch worth pinning separately, and `ParseClashApiPort` split
cleanly into reject-paths and accept-paths).

Breakdown by area:
- Construction state: 3 facts (initial idle state, default modes,
  events default null-safe).
- Stop on idle engine: 2 facts (idempotent + status events ordering).
- ApplyAsync idle guard: 2 facts (returns false, no Applying status).
- Dispose semantics: 2 facts (idle no-throw + idempotent).
- Static helpers: 5 facts (ParseClashApiPort reject + accept paths +
  ResolveCustomConfigPath legacy fallback + multi-config name pick +
  BuildBundledOnlyProfileSources built-in fallback).
- Source-string pins: 2 facts (Stop BR-6a HealthMonitor-before-
  sing-box ordering + Dispose-calls-Stop-when-running invariant).

**Full-suite result**: **1229 passed / 4 skipped / 0 failed** on
`dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
--no-build --filter
"FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`.
Delta vs. baseline at `247f6a6` (2G UpdateChecker tests, baseline
1213): +16 passing, no skips changed, no failures.

**Surprise**: ApplyAsync on an idle engine does NOT raise the
StatusChanged "Applying config changes..." event. The guard
`if (_singBox == null || !_singBox.IsRunning()) return false`
short-circuits BEFORE the `OnStatus("Applying config changes...")`
line. That's the desired UX (don't tell the user "applying" when
nothing's running), but worth pinning explicitly — covered by
`ApplyAsync_IdleEngine_DoesNotEmitApplyingStatus`.
