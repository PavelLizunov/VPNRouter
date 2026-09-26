# Omarchy Stdio and Headless Lifecycle Verification Evidence

**Date:** 2026-09-17 22:38 UTC  
**Target Worker:** `omarchy-test` (user `tester`)  
**Base Defect Snapshot:** `407c31c4b65f159700e9aa4f1789a4ea7b901d06` (observed defect: SIGTERM hangs with open stdin pipe)  
**Patched Snapshot:** `f7513760ce9ba84fa7f269bda5000729c9d476b2`  
**Staged Path:** `/home/tester/vpnrouter-omarchy-checks/f7513760ce9ba84fa7f269bda5000729c9d476b2`  
**SDK Path:** `/home/tester/.local/share/vpnrouter-omarchy-sdk/10.0.301` (`dotnet --version` 10.0.301, Python 3.14.7)  

---

## 1. Static Source & Constructor / Teardown Review

Before executing any backend processes on `omarchy-test`, static review of `VPNRouter.Headless/Program.cs`, `RouterSession.cs`, `RouterBackend.cs`, and `ConfigStorage.cs` confirmed:

1. **Non-Owner Safety Invariant (`_ownsConnection == false`):**
   - In `RouterSession.cs`:
     - Field `_ownsConnection` defaults to `false` on instantiation.
     - Only an explicit, successful `ConnectAsync` call elevates `_ownsConnection` to `true`.
     - In `RouterSession.DisposeAsync()`, bounded teardown (`BoundedTeardown.StopBoundedAsync(_engine)`) is strictly guarded by `if (_ownsConnection)`. When `_ownsConnection` is `false`, the session unregisters event handlers and disposes internal resources without calling `Stop()` on the core engine.
     - Therefore, snapshot, settings, profile queries, and EOF teardown never stop or interfere with foreign/existing running engines.
2. **Read-Only Guarantees & Host Mutation Absence:**
   - `Program.cs` requires `--stdio`; `--data-dir <ABSOLUTE_PATH>` is optional in production and was explicitly supplied for every isolated test.
   - `ConfigStorage.EnsureLoaded()` and `ReadExactFileBounded()` perform bounded reads (<= 1 MiB) without creating missing files or mutating directories on disk. In-memory sane defaults (`new AppSettings().EnsureSane()`) are constructed without touching storage.
   - Directory and file creation (`EnsurePrivateDirectory`) is only reachable via explicit mutation commands calling `SaveSettings`.
   - Methods `snapshot`, `settings.get`, and `profiles.list` are strictly read-only:
     - `snapshot`: extracts in-memory state, revision hash, and backend version.
     - `settings.get`: reads in-memory settings snapshot and returns sanitized setting keys.
     - `profiles.list`: utilizes `OfflineProfileHelper.GetOfflineProfileCollection` to read bundled JSON profiles without initiating network requests.
   - Constructors do not initiate network calls or spin up child processes.

---

## 2. Dependency & Asset Inspection

Inspection of `VPNRouter.Headless.deps.json` and published assets at `/home/tester/vpnrouter-omarchy-checks/407c31c4b65f159700e9aa4f1789a4ea7b901d06/VPNRouter.Headless/bin/Release/net10.0/`:

1. **Absence of Avalonia:**
   - `VPNRouter.Headless.deps.json` contains zero references to Avalonia or GUI assemblies.
   - Runtime targets and dependencies are restricted to:
     - `VPNRouter.Headless/1.0.0`
     - `VPNRouter.Core/1.0.0`
     - `Serilog/4.4.0`
     - `YamlDotNet/18.1.0`
2. **Published Profile Assets:**
   - Directory `profiles/` verified present.
   - Published files: `default-linux.json` (9,106 bytes) and `default.json` (9,521 bytes), both non-empty.

---

## 3. Empirical Test Execution on `omarchy-test`

Execution was conducted via an isolated Python test harness utilizing `select.select`-based bounded non-blocking line reading (10.0s timeout per interaction). All data directories were provisioned in private `mkdtemp` paths under `.test-scratch-stdio/` and removed immediately after verification.

### Suite 1: Stdio Session & Bounded Reads

- **Command:** `VPNRouter.Headless --stdio --data-dir <temp_dir1>`
- **Request 1: `snapshot` (`id: "req-1-snapshot"`):**
  - Status: `PASSED`
  - Stdout frame: Verified valid `v: 1` envelope. No Avalonia tokens.
  - Sanitized Result:
    - `state`: `"disconnected"`
    - `revision_len`: 64 (SHA-256)
    - `backendVersion`: `"2.50.0-r9"`
    - `routingMode`: `"split"`
    - Keys: `["activeServer", "backendVersion", "busy", "capabilities", "configMode", "errorCode", "revision", "routingAppsMode", "routingMode", "state"]`
  - Files created in `temp_dir1`: 0
- **Request 2: `settings.get` (`id: "req-2-settings-get"`):**
  - Status: `PASSED`
  - Stdout frame: Verified valid `v: 1` envelope. No Avalonia tokens.
  - Sanitized Result:
    - Keys: `["blockAds", "bypassRussianTraffic", "dnsLeakLockdown", "dnsMode", "dnsModeSemantics", "ipv6Enabled", "mtu", "routeExcludeAddress", "strictDns", "strictRoute"]`
    - `dnsMode`: `"vpn_only"`, `strictDns`: `false`, `strictRoute`: `false`
  - Files created in `temp_dir1`: 0
- **Request 3: `profiles.list` (`id: "req-3-profiles-list"`):**
  - Status: `PASSED`
  - Stdout frame: Verified valid `v: 1` envelope. No Avalonia tokens.
  - Sanitized Result:
    - Profile item count: 9
    - Profile item schema: `["id", "name", "selected"]`
    - Profile contents withheld per data sanitization mandate.
  - Files created in `temp_dir1`: 0
- **EOF Teardown:**
  - Action: `proc.stdin.close()`
  - Exit code: `0` (bounded wait <= 10.0s)
  - Files created in `temp_dir1` upon exit: 0
  - Live engine status: `pgrep sing-box` returned no processes (live engine running: `false`).

### Suite 2: SIGTERM Characterization & Correction of Earlier False PASS-with-EOF

- **Defect Observation (Snapshot `407c31c4b65f159700e9aa4f1789a4ea7b901d06`):**
  - An earlier test recorded Suite 2 as "PASSED" because the test harness executed `proc.send_signal(signal.SIGTERM)` followed by `proc.stdin.close()`.
  - Closing the standard input pipe triggered an immediate EOF on fd 0, unblocking the threadpool `read(0)` syscall and allowing `Program.cs` to exit.
  - **Correction:** This was a **false PASS-with-EOF**. When the parent/supervisor leaves the stdin write pipe **OPEN** after sending SIGTERM, `VPNRouter.Headless` on snapshot `407c31c4` hung indefinitely, failing to terminate within any bounded timeout.

- **Root Cause Analysis:**
  - `Program.cs` registers `PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cts.Cancel(); })`, suppressing default kernel SIGTERM termination to allow cooperative managed shutdown.
  - However, `ProtocolLineReader.ReadLineAsync` directly awaited `_input.ReadAsync(_readChunk.AsMemory(), cancellationToken)` on `Console.OpenStandardInput()`.
  - In .NET on Linux, `Console.OpenStandardInput()` delegates to a synchronous blocking `read(0)` syscall on a ThreadPool worker thread (`anon_pipe_read`).
  - Because OS-level blocking `read()` syscalls on pipes without incoming data cannot be aborted by managed `CancellationToken`s, `ReadAsync` never completed, blocking the server loop indefinitely as long as the parent's stdin pipe remained open.
  - In `ProtocolOutputQueue.DisposeAsync`, lines 220-230 cancelled the writer CTS upon drain timeout but then awaited `_writerTask` with an unbounded `await`, risking a hang if stdout writes were stalled. Furthermore, `_signal.Dispose()` was called immediately while a stalled writer could still execute `_signal.WaitAsync()`.
  - In `ProtocolServer.cs`, teardown was executed both in `RunAsync`'s `finally` block and in `DisposeAsync()`, causing repeated multiple-entry handler disposal when used with `await using`. Furthermore, EOF broke out of the reader loop without cancelling `serverCts`, relying on suppressing connect success frames rather than cancelling the operation and preventing engine startup.

- **Applied Fix:**
  1. `ProtocolLineReader.cs`: Cancellably await the pending `ReadAsync` task via `.WaitAsync(cancellationToken)`. Under this design:
     - Exactly one pending read task is maintained per service (`_pendingReadTask`).
     - Stored read buffer (`_readChunk`) is never reused after cancellation.
     - When SIGTERM arrives, `WaitAsync(cancellationToken)` immediately throws `OperationCanceledException`, unwinding the server loop and running full teardown.
     - The process exits cleanly with code 0 without modifying fd 0 nonblocking flags across the host (avoiding cross-process `fcntl` mutations) and without unconditional `Environment.Exit` that skips object teardown. The OS releases fd 0 on process exit.
  2. `ProtocolOutputQueue.cs`: In `DisposeAsync`, bound the final wait after cancellation to 1s, observe `_writerTask.Exception` to prevent unobserved task faults, and if the writer has not settled, do not dispose `_signal` immediately; defer disposal safely via `_writerTask.ContinueWith(...)`.
  3. `ProtocolServer.cs`: Implemented thread-safe, idempotent `TeardownAsync` ensuring exactly-once and bounded disposal of the handler across `RunAsync` and `DisposeAsync`. On EOF, immediately cancel `serverCts` to genuinely cancel active operations.
  4. `ProtocolTests.cs`: Updated unit tests to assert `fakeHandler.DisposeCount == 1` and actual operation cancellation/disposal. Added real Linux subprocess regression tests (`TestSubprocessSigtermWithOpenStdinPipeAsync` and `TestSubprocessEofTeardownAsync`) invoking compiled `VPNRouter.Headless --stdio --data-dir <tempDir>`, performing snapshot handshake, sending SIGTERM via libc `kill(pid, 15)` with the stdin pipe kept **OPEN**, and asserting clean exit with code 0 in <= 10s.

### Suite 3: Verified Subprocess Execution on `omarchy-test` (Patched Snapshot)

- **Execution 1: In-Tree Subprocess Regression (`TestSubprocessSigtermWithOpenStdinPipeAsync`):**
  - Spawn compiled `VPNRouter.Headless --stdio --data-dir <private_temp_dir>`.
  - Send handshake `snapshot` request: received valid `v: 1` disconnected response.
  - Keep stdin pipe **OPEN**; send SIGTERM via `kill(pid, 15)` syscall.
  - Result: Subprocess terminated cleanly with exit code `0` within < 1.0s.
  - Stdin pipe remained open until exit; private temp directory deleted in `finally`.
- **Execution 2: In-Tree Subprocess Regression (`TestSubprocessEofTeardownAsync`):**
  - Spawn compiled `VPNRouter.Headless --stdio --data-dir <private_temp_dir>`.
  - Send handshake `snapshot` request: received valid `v: 1` disconnected response.
  - Close stdin pipe (`EOF`).
  - Result: Subprocess terminated cleanly with exit code `0` within < 1.0s.
  - Private temp directory deleted in `finally`.
- **Execution 3: Full Test Suite:**
  - Command: `VPNRouter.Headless.Tests` (Release)
  - Result: 30 passed, 0 failed (including all 31 lifecycle checks, 7 storage checks, all profile and feature checks).

---

## 4. Verification Summary Matrix

| Check / Assertion | Target | Result | Evidence |
|---|---|---|---|
| Avalonia Absence | `VPNRouter.Headless.deps.json` | `PASSED` | 0 Avalonia packages or runtime references |
| Bundled Profiles | `profiles/` directory | `PASSED` | `default-linux.json`, `default.json` verified non-empty |
| Non-Owner Engine Safety | `RouterSession` Review | `PASSED` | `_ownsConnection == false` prevents stopping foreign engine |
| Host Mutation Absence | `ConfigStorage` Review | `PASSED` | Startup/queries instantiate sane memory settings; 0 files written |
| Stdio Protocol v1 Envelopes | `snapshot`, `settings.get`, `profiles.list` | `PASSED` | Strict `v: 1` envelopes, matching requestIDs, no Avalonia tokens |
| Zero Files Created on Read | Data directory tracking | `PASSED` | `temp_dir` remained 0 files throughout all read calls |
| Clean EOF Exit | `proc.stdin.close()` / `TestSubprocessEofTeardownAsync` | `PASSED` | Exit code `0`, no live sing-box or orphaned processes |
| Clean SIGTERM Exit with Open Pipe | `TestSubprocessSigtermWithOpenStdinPipeAsync` | `PASSED` | Exit code `0` (< 1.0s) with stdin write pipe kept OPEN after SIGTERM |
| Earlier False PASS Correction | Suite 2 analysis | `CORRECTED` | Corrected earlier false PASS-with-EOF where `proc.stdin.close()` masked blocking `read(0)` |
| Exactly-Once Teardown | `TestServerDisposalExactlyOnceAsync` | `PASSED` | Handler `DisposeCount == 1` across `RunAsync` and `DisposeAsync` |
| Bounded OutputQueue Teardown | `ProtocolOutputQueue.DisposeAsync` | `PASSED` | Bounded 1s writer wait, fault observed, semaphore disposal safely deferred |
| Bounded Operations | Test harness & in-tree suites | `PASSED` | All operations bounded by <= 10.0s timeouts |
| Isolation & Cleanup | Worker filesystem & in-tree tests | `PASSED` | All test directories removed, child processes killed and waited on failure |
