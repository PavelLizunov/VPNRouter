# Phase 2G — `SingBoxManagerStateMachineTests` (Phase 2G closure)

**Owner**: Claude session (Opus 4.7, 1M context)
**Branch**: `main` (test-only addition, zero behavioural risk)
**Roadmap ref**: Phase 2G coverage audit closure.
`VPNRouter.Core/Services/SingBoxManager.cs` is 1133 LOC and had **no
dedicated unit tests** — only `SingBoxManagerRestartTunHandshakeTests`
which is source-string-pin-only for the Wave-38 TUN-adapter hotfix.
Phase 3G-2 (commit `5370be3`) unblocked this gap by adding an
optional `IHttpClient http = null` ctor parameter; tests can now
inject `FakeHttpClient` to exercise the Clash API hot-reload + liveness
probe without spawning a real sing-box process.
**Predecessors**: `phase2G-updatechecker-tests-2026-05-21.md` (commit
`247f6a6`, +19 tests), `phase2G-vpnengine-orchestrator-2026-05-21.md`
(commit `14c512e`, +16 tests).
**Effort**: ~45 min.
**Risk**: NONE (test-only; no production code is modified).
**Blast radius**: 1 new file
(`VPNRouter.Tests/SingBoxManagerStateMachineTests.cs`).
**Rollback**: `git revert <commit>` — drops the test file.

## Why

Phase 2G audit (2026-05-20) flagged `SingBoxManager.cs` as the highest-LOC
service in Core with zero dedicated unit tests. It owns the process
lifecycle (start / stop / restart / hot-reload) — the v2.31.x recovery
work concentrated here, and the Stop/Restart race + Clash hot-reload
fallback are the leak-class paths.

Until Phase 3G-2 there was no test seam: the class held a `static readonly
HttpClient` pointed at `127.0.0.1:9090`, so exercising the hot-reload
path required spawning a real sing-box. 3G-2's optional `IHttpClient`
parameter (default `PolicyHttpClient.Shared`) makes the HTTP surface
test-injectable. `FakeHttpClient.Setup(url, body)` now stubs Clash API
responses in-memory.

What's STILL not unit-testable (deferred to Phase 3+ when a process-
factory seam lands): the actual `LaunchProcess` → `_process.Start()`
path. SingBoxManager spawns sing-box via `System.Diagnostics.Process`
with no factory interception; without that seam, the in-test `_process`
field is always null and `TryHotReload`'s `_process == null ||
HasExited` early return short-circuits before any HTTP call. **This
brief therefore separates the two layers**:

1. The **state-machine surface** — construction state, idle Stop /
   Dispose / Restart no-ops, `Pid` null-safety, `IsRunning` / `IsHealthy`
   / `GetMetrics` defaults — invoke-tested directly.
2. The **HTTP-routing surface** (Clash API liveness + hot-reload PUT)
   — exercised via reflection that pokes a non-null `_process` field
   so the early-return guard doesn't short-circuit. This is brittle
   per `simplify`'s usual taste, but it's the ONLY way today to hit
   the byte-shape of the Clash API request, which is exactly what the
   3G-2 regression pin needs.

## What

Single new file: `VPNRouter.Tests/SingBoxManagerStateMachineTests.cs`.
Plain `[Fact]` xUnit — no Avalonia headless dispatcher, no file system.

### Test categories

1. **Construction state** (2 tests). `State == Stopped` after ctor; `Pid`
   returns `null` when `_process` is null. Pin the public surface the
   UI sees BEFORE the user clicks Connect.

2. **`IsRunning` / `IsHealthy` / `GetMetrics` defaults on idle engine**
   (3 tests). On Windows: `IsRunning == false` (State guard); on all OS
   `IsHealthy == false` (null process); `GetMetrics` returns default
   `ProcessMetrics` (zero memory, zero CPU, null start time).

3. **Idle Stop is no-op + idempotent** (2 tests, Windows-only). On the
   Linux/macOS path `Stop()` shells out to `pkexec`/`pkill`/`sudo` via
   `LinuxStopEscalationChain` which spawns external processes —
   intractable for a unit test. Windows-only branch falls cleanly into
   the `_process == null || HasExited` cleanup path. The orphan-adapter
   netsh call inside that branch is best-effort and `try`-wrapped, so
   it can't make the test throw.

4. **Idle Dispose is no-op + idempotent** (2 tests, Windows-only).
   Same gating rationale as Stop. Dispose internally calls Stop;
   second Dispose hits the `_disposed` guard.

5. **`IsClashApiAlive` HTTP routing** (3 tests). Reflection-invoke on
   the private method, FakeHttpClient stubs:
   - 200 → true.
   - 500 → false.
   - HTTP exception (`HttpRequestException` simulating timeout /
     transport error) → false (the catch-all swallows).

6. **`TryHotReload` early returns when process is null** (1 test). No
   HTTP stub — verifies the line-551 `_process == null || HasExited`
   guard prevents an HTTP call entirely. Uses `FakeHttpClient` so any
   accidental call would throw "no route registered" (loud failure).

7. **`TryReloadConfigJson` writes JSON to disk + returns false on null
   process** (1 test). Pin the public path's behaviour for the
   no-process-yet case (e.g. caller forgot to Start). Writes JSON then
   short-circuits at TryHotReload — net effect: returns false but the
   on-disk `current.json` is updated. (The JSON write is a sanity
   side-effect; we only assert the return value to avoid coupling to
   `%ProgramData%` paths.)

8. **`IHttpClient` injection regression pin** (3G-2) (1 test). Construct
   SingBoxManager with `FakeHttpClient`, invoke a code path that calls
   `_http.SendAsync` (reflection on `IsClashApiAlive` with a non-null
   `_process` so the guard passes), then assert `FakeHttpClient.SentRequests`
   captured the call. Prevents anyone re-adding a `static readonly
   HttpClient` field that bypasses the injected seam.

9. **Clash API URL + body shape pin** (1 test). The 3G-2 migration
   preserved byte-for-byte URL + body shape so the sing-box Clash API
   keeps accepting it. Pin:
   - URL = `http://{ClashApi}/configs?force=true`
   - Body = `{"path":"<escaped-path>"}` JSON
   - `Method = PUT`
   - `BodyContentType = application/json`
   Uses reflection to invoke `TryHotReload` after poking `_process`
   non-null + `_currentConfigPath` set.

10. **Source-string pin — `EnableRaisingEvents = false` before `Kill`**
    (1 test). The CLAUDE.md "Intentional stop" pattern — pre-Stop sets
    EnableRaisingEvents=false so the Exited callback can't fire as a
    false crash. Source-pin via the existing pattern from
    `SingBoxManagerRestartTunHandshakeTests`.

11. **Source-string pin — Restart preserves TUN lock** (1 test). The
    `StopInternal(releaseLock: false)` call in `Restart()` is the
    invariant that prevents another instance from grabbing the lock
    during the brief Stop→LaunchProcess window. Source-pin.

12. **Source-string pin — `_http` field is a non-static instance member**
    (1 test). Direct regression pin for the 3G-2 migration: ensures
    nobody re-introduces a `static readonly HttpClient _http` (or any
    static httpclient field) that would bypass DI/test injection.

**Total**: ~16 tests.

### Coverage matrix vs. brief categories from the task

| Brief category | Covered by | Notes |
|---|---|---|
| 1. Initial state | Construction state (2) | Direct invoke |
| 2. Stop on Stopped is no-op | Idle Stop is no-op (Windows-only) | Linux path needs IProcess seam |
| 3. Dispose on Stopped is no-op | Idle Dispose is no-op (Windows-only) | Linux path needs IProcess seam |
| 4. Dispose is idempotent | Idle Dispose idempotent | Pin `_disposed` guard |
| 5. EnableRaisingEvents before Kill | Source-string pin | Can't behaviour-test without IProcess seam |
| 6. IsClashApiAlive returns true on 200 | HTTP routing (3) | Reflection invoke |
| 7. IsClashApiAlive returns false on 5xx | HTTP routing (3) | Same |
| 8. IsClashApiAlive returns false on timeout | HTTP routing (3) | HttpRequestException injected via FakeHttpClient.ThrowOn |
| 9. ReloadConfig PUT happy path + URL/body shape pin | Clash API URL + body shape pin | Reflection invoke into TryHotReload |
| 10. ReloadConfig PUT 4xx body bubbles to log | HTTP routing 500 case | Log assertion deferred — minimum pinned: returns false, doesn't throw |
| 11. TryHotReload returns false if process null | Early-return guard test | Direct invoke, FakeHttpClient asserts no HTTP call |
| 12. IHttpClient is injected, not static | Injection regression pin + source-string pin | Pair: behaviour test + source pin |

## How

1. **Create** `VPNRouter.Tests/SingBoxManagerStateMachineTests.cs` with:
   - `#nullable enable` header.
   - `using System.Diagnostics;` (for `Process`, used as the non-null
     placeholder via reflection — we use `Process.GetCurrentProcess()`
     so `HasExited == false`).
   - `using System.Net.Http;` for `HttpRequestException`.
   - `using System.Reflection;` for private field/method access.
   - `using VPNRouter.Core.Models;` for `SingBoxSettings`.
   - `using VPNRouter.Core.Services;` for `SingBoxManager` /
     `SingBoxState` / `IHttpClient` / `HttpRequest` / `HttpResponse`.
   - `using VPNRouter.Tests.Fakes;` for `FakeHttpClient`.

2. **Linux/macOS gating**: tests that call `Stop()` / `Dispose()` are
   wrapped in an `if (!OperatingSystem.IsWindows()) return;` guard at
   the top of the test body — quietly skips on Linux CI rather than
   adding `[Trait]` infra. The HTTP-routing tests work on all OS so
   no gating needed for those.

3. **Reflection helpers** — small private statics that:
   - `GetField<T>(SingBoxManager m, string name)` — reads private field.
   - `SetField<T>(SingBoxManager m, string name, T? value)` — writes
     private field. Used to poke `_process` non-null + `_currentConfigPath`
     so `TryHotReload`'s early-return guard doesn't short-circuit.
   - `InvokePrivate<T>(SingBoxManager m, string method, params object[] args)`
     — invokes private method via reflection, unwraps the return value
     and surfaces inner exceptions cleanly.

4. **Process placeholder**: use `Process.GetCurrentProcess()` for the
   `_process` non-null poke. The test host's own process won't have
   exited, so `HasExited == false` and the guard passes. **We never
   Kill or Stop this process** — it's just a non-null reference for
   the field. The Dispose contract on Process doesn't actually do
   anything harmful for the current process handle.

5. **No filesystem touches** in the HTTP-routing tests. The
   `TryReloadConfigJson` test does touch `%ProgramData%\VPNRouter\config\
   current.json` (via `WriteJsonToDisk`); on a clean CI runner without
   that directory the write will create it. To avoid coupling to
   `%ProgramData%` paths, that test pre-creates the directory if
   missing and asserts only on the return value, not the file
   contents. We rely on the v3.0 Android port's `AppPaths.OverrideDataDir`
   to keep the path well-formed.

### Verification approach

- `dotnet build VPNRouter.sln -c Release` → 0 errors.
  `taskkill /F /IM testhost.exe` first if locks reported.
- `dotnet test ... --filter
  "FullyQualifiedName~SingBoxManagerStateMachineTests"` → all new
  tests green.
- Full suite: `dotnet test ... --filter
  "FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
  expects 1245+ pass (baseline at `14c512e` was 1229).

## Verification gate

Check off each as you complete:

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [x] **Gate 2 — Tests green**: new tests pass; full suite green at the
  baseline established in `phase2G-vpnengine-orchestrator-2026-05-21.md`.
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

- **Real process spawn path** (`LaunchProcess` → `Process.Start` →
  process state transitions to `Running`). Requires an `IProcessRunner`
  / `IProcess` seam that doesn't exist yet. Phase 3+ follow-up. See
  `SingBoxManagerRestartTunHandshakeTests` for source-string pins
  covering the LaunchProcess → PreStartCleanup chain.
- **Crash detection event timing** (Exited callback fires → `OnProcessExited`
  → `Crashed` event). Requires the IProcess seam to inject a fake
  process whose Exited event we can fire on demand. Deferred.
- **HealthMonitor + SingBoxManager coupling**. Cross-covered by
  `HealthMonitorRecoveryGapTests` (which uses its own SingBoxManager
  stub).
- **Linux pkexec escalation chain**
  (`LinuxStopEscalationChain` + `TrySpawnAndWait`). External-process-
  heavy; no plausible test seam. Behaviour is exercised end-to-end on
  the Linux CI runner via integration tests (`tools/test-linux.sh`).
- **`LogSingBoxCrashTail`**. Reads `singbox.log` from `%ProgramData%`
  on disk — adds fixture overhead. Best-effort by design (returns
  silently on any I/O error per the doc comment); the leak class
  (vpnrouter.log getting flooded by GB-scale singbox.log) is bounded
  by the 50-line tail constant.
- **`HasNetCapability`** (Linux-only `getcap` probe). External-process
  call; would need an IProcessRunner seam.

## Outcome (filled 2026-05-21)

**Status**: PASS — 19 tests added, all green.

**Commit**: `7a5420c` test(singbox): 2G — 19 state-machine characterization tests

**Test deltas**: +19 in
`VPNRouter.Tests/SingBoxManagerStateMachineTests.cs` (three extras
vs. the ~16 estimate — the source-pin layer split into three
discrete facts (intentional-stop ordering, Restart preserves TUN
lock, IHttpClient field is non-static) once the pattern landed,
because each carries a distinct rationale + a separate regression
window).

Breakdown by area:
- Construction state: 2 facts (initial Stopped state + Pid null on
  idle).
- IsRunning / IsHealthy / GetMetrics defaults: 3 facts (idle returns
  false on Windows + null process IsHealthy false + zero metrics).
- Idle Stop is no-op + idempotent: 2 facts (Windows-only gating).
- Idle Dispose is no-op + idempotent: 2 facts (Windows-only gating).
- IsClashApiAlive HTTP routing: 3 facts (200 → true, 500 → false,
  HttpRequestException → false).
- TryHotReload guard short-circuits when process is null: 1 fact
  (FakeHttpClient asserts no HTTP call leaked through).
- TryReloadConfigJson returns false on null process: 1 fact.
- IHttpClient injection regression pin (behaviour test): 1 fact.
- Clash API URL + body shape pin (3G-2 wire compat) via reflection
  poke of _process non-null: 1 fact.
- Source-string pins: 3 facts (intentional-stop ordering pre-Kill +
  Restart preserves TUN lock + IHttpClient _http field is non-static).

**Full-suite result**: **1248 passed / 4 skipped / 0 failed** on
`dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
--no-build --filter
"FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`.
Delta vs. baseline at `14c512e` (2G VpnEngineOrchestratorTests):
+19 passing, no skips changed, no failures.

**Surprise**: the first cut of the EnableRaisingEvents-before-Kill
source pin used an exact string match (with embedded `\n` line
terminators) and failed because the repo checkout uses CRLF on
Windows. Generalised the pin to strip line comments + collapse
whitespace, then assert the ordering of two index positions — same
contract, robust to line-ending and indentation drift.
