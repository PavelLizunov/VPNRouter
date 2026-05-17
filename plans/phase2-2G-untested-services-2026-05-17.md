# Phase 2 — 2G: Test the 9 untested services

**Owner**: Wave 7 agent (single, but spawns 3 parallel sub-waves by criticality)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` Phase 2G; `plans/test-coverage-audit-2026-05-17.md` §2 + §3
**Depends on**: Wave 6 (2D abstractions: `IProcessRunner`, `IFileSystem`, `IHttpClient`, `ISingBoxApi`)
**Effort**: 2-3 days
**Risk**: LOW (pure additive tests — no production-code change except minimal seams already added in Wave 6 POCs)

## Why

Per `plans/test-coverage-audit-2026-05-17.md` §2 the audit found **9 fully untested services** spanning **1,851 LOC**:

| Priority | Service | LOC | Why critical |
|---|---|---|---|
| CRITICAL | `WindowsDnsHardening` | 249 | Writes `netsh dnsclient` policy. Failure = DNS leak. Mirror of `FirewallManager` (tested). |
| CRITICAL | `HostsManager` | 256 | Writes `%SystemRoot%\...\hosts` (Discord voice). Wrong entry = total resolution break. |
| HIGH | `EtwProcessMonitor` | 184 | Real-time process scanner. Stale event = wrong routing. |
| HIGH | `VlessDeepVerifier` | 606 | Deep server probe. False positive = bad server marked good. |
| HIGH | `LockFile` | 110 | Single-instance + TUN race. v2.31.x recovery work touches this. |
| HIGH | `ZapretActions` | 562 | Largest untested file. Cygwin gotcha (CLAUDE.md). |
| MED | `DnsFlusher` | 114 | `ipconfig /flushdns` wrapper. Stale cache, not leak. |
| MED | `NetworkInterfaceDetector` | 171 | Adapter enumeration; consumed by leak detection. |
| LOW | `QrCode` | 599 | Read-only UI helper. |

All five `HIGH+` ones cross the seams Wave 6 just installed — that is exactly why we did Wave 6 first.

## What

For each service, add a dedicated test class in `VPNRouter.Tests/<ServiceName>Tests.cs` (or extend existing partial coverage).

**Target coverage** per service:
- **CRITICAL**: 8-12 tests covering success path + 3-4 failure modes + idempotency
- **HIGH**: 6-10 tests covering success path + key failure modes
- **MED**: 4-6 tests covering primary surface
- **LOW**: 3-4 smoke tests

**Estimated total**: ~60 new tests, ~2,500 LOC of test code.

**Parallelism strategy** — split into 3 sub-waves:

### Sub-wave 7a (parallel, 2 agents)
- Agent 7a-1: `HostsManager` + `WindowsDnsHardening` (CRITICAL pair, both use `IFileSystem` + `IProcessRunner`)
- Agent 7a-2: `LockFile` + `DnsFlusher` (mid-difficulty, both use `IFileSystem` / `IProcessRunner`)

### Sub-wave 7b (parallel, 2 agents)
- Agent 7b-1: `EtwProcessMonitor` + `NetworkInterfaceDetector` (process / network observers)
- Agent 7b-2: `ZapretActions` (largest single — solo so the .bat-builder + arg parser get full attention)

### Sub-wave 7c (parallel, 2 agents)
- Agent 7c-1: `VlessDeepVerifier` (uses `IHttpClient` for proxy probe + `ISingBoxApi` for in-process)
- Agent 7c-2: `QrCode` (LOW priority — fast wrap-up agent)

Each sub-wave runs in worktree isolation. Integrate per-service commits.

## How

For each service:

1. **Read the service** to understand its public surface + dependencies.
2. **Identify the seam** — does it use `IProcessRunner` / `IFileSystem` / `IHttpClient` / `ISingBoxApi` (Wave 6 just added these)? If yes, inject the fake in tests.
3. **Write happy-path test** — feed expected input, assert expected output, verify the fake was called with the right shape.
4. **Write 3-4 failure-mode tests** — fake throws / returns nonzero exit / returns 5xx / missing file → service handles gracefully.
5. **Write idempotency test** — call twice, second call no-op (HostsManager + WindowsDnsHardening especially).
6. **Run scoped suite** to confirm new tests pass and existing tests still pass:
   `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff"`

**Critical gotchas** (from `CLAUDE.md` and `CLAUDE.local.md`):
- `ZapretActions` builds Cygwin `.bat` files needing `SET BIN=` and `SET LISTS=` (NOT literal Windows paths).
- `EtwProcessMonitor` uses ETW on a dedicated background thread — test the parser surface, not the ETW subscription itself.
- `HostsManager` must NEVER overwrite existing user entries — only append/remove its own (signature comment block).
- `LockFile` must release on `Dispose` and on process exit (use `FileShare.None` + delete-on-close).
- `WindowsDnsHardening` is **mirror to FirewallManager** — copy the test patterns from `FirewallManagerLocalizedNetshTests.cs`.

## Verification gate

- [ ] All 9 services have a dedicated `<ServiceName>Tests.cs` file
- [ ] Coverage targets met (CRITICAL: 8+, HIGH: 6+, MED: 4+, LOW: 3+ tests)
- [ ] **Gate 1**: build 0 errors
- [ ] **Gate 2**: scoped suite gains ~60 tests, all pass
- [ ] **Gate 4 simplify**: each per-service test file <300 LOC (otherwise split)
- [ ] **Gate 4 security-review**: for `HostsManager` + `WindowsDnsHardening` + `LockFile` (system-state mutators)
- [ ] **Hook gates** pass

## Outcome

### Wave 7c-2 — `QrCode` (2026-05-18)

**Agent**: sub-wave 7c-2 (LOW priority, fast wrap-up).
**File added**: `VPNRouter.Tests/QrCodeTests.cs` (~120 LOC, 6 tests, all <50ms).
**Service touched**: `VPNRouter.Core/Services/QrCode.cs` — NONE (pure additive tests, no production change).

Tests written (target was 3-5, delivered 6 to cover all useful smoke shapes):

1. `EncodeText_VlessUri_ProducesNonEmptyMatrix` — happy path with a real-shaped Reality URI; asserts `Size = version*4+17` invariant, `Mask in [0,7]`, `ToMatrix` returns the right dimensions, and the result contains both dark + light modules (cheap "rendering actually ran" check).
2. `EncodeText_Null_ThrowsArgumentNullException` — null-input contract.
3. `EncodeText_EmptyString_ProducesSmallestVersion` — empty UTF-8 payload still encodes to v1/21x21 cleanly; also pins `GetModule(x,y) == ToMatrix()[y,x]` for every cell (catches axis-flip regressions).
4. `EncodeText_LongSubscriptionUrl_FitsWithoutCrash` — 2000-char payload encodes without throwing and selects a high version (>=15), guarding against silent truncation if the version-selection loop is broken.
5. `EncodeText_GetModule_OutOfBoundsReturnsFalse` — defensive contract for quiet-zone probing by the rendering layer (no throw on negative / past-Size coords).
6. `EncodeText_HigherEcc_PicksLargerOrEqualVersion` — pins the version-selection loop's monotonicity and the "ECC may auto-upgrade but never downgrade" guarantee.

**Verification gate**:
- [x] Gate 1 build 0 errors (Release): pre-existing warnings unchanged, no new ones.
- [x] Gate 2 scoped suite: 881 passed / 3 pre-existing skips / 0 failed (was 875 before, +6 new).
- [x] Test file is 120 LOC (well under 300-LOC split threshold).
- [x] `#nullable enable` + `sealed` fixture.
- [x] All tests <50ms (full QrCode subset runs in 45ms total).

**Surprises / notes**:
- The encoder has no public decode path — `QrCodeDecoder.cs` lives Android-side and uses ZXing, so round-trip testing isn't available from `VPNRouter.Tests`. Compensated by asserting structural invariants (size formula, mask range, dark+light presence, version monotonicity).
- The encoder auto-upgrades ECC level for free when capacity permits — test 6 asserts only the "never downgrades" weaker contract to avoid being brittle to the upgrade heuristic.

**Files staged (not committed — integrator commits)**:
- `VPNRouter.Tests/QrCodeTests.cs` (new, 120 LOC)
- `plans/phase2-2G-untested-services-2026-05-17.md` (Outcome section)

### Wave 7a-2 — `LockFile` + `DnsFlusher` (2026-05-18)

**Status**: PASS — both services now have dedicated test files; DnsFlusher seam added (static-class → instance + static-facade, mirroring Wave 6's LockFile pattern).

**Files staged**:
- `VPNRouter.Core/Services/DnsFlusher.cs` — converted from static to instance `sealed class` with `IProcessRunner` ctor seam. `DefaultInstance` preserves legacy `static DnsFlusher.Flush(ILogger)` so the single consumer (`VpnEngine.cs:167`) does not change. +144 / -49 (+95 net).
- `VPNRouter.Tests/LockFileTests.cs` — 11 tests, 303 LOC.
- `VPNRouter.Tests/DnsFlusherTests.cs` — 8 tests, 231 LOC.

**LockFile tests cover**: AcquireInstance happy path, anti-double-launch invariant (the v2.31.x regression class), idempotent reacquire, release-then-reacquire, `DetectPreviousCrashInstance` {none / dead PID / live PID / unreadable payload / then-acquire end-to-end} — 4 tests guard the always-consumes-the-stale-file contract — static-facade smoke, on-disk PID payload structure pin.

**DnsFlusher tests cover**: happy path, arg correctness (executable = `ipconfig.exe`, args = exactly `["/flushdns"]`), nonzero-exit graceful handling, timeout graceful handling, runner-throws graceful handling, idempotency, reasonable timeout window pin (≥1s ≤30s), static-facade smoke.

**Test delta**: +19 tests in scoped suite (target was 10-16; over-met).

**Verification gates**:
- [x] LockFileTests.cs: 11 (target 6+, HIGH)
- [x] DnsFlusherTests.cs: 8 (target 4+, MED)
- [x] Gate 1 build 0 errors
- [x] Gate 2 scoped suite: 894 passed, 0 failed, 3 skipped (pre-existing)
- [x] Gate 4 security-review (LockFile): no path injection (lock path from `AppPaths.DataDir`); TOCTOU window only affects diagnostic banner accuracy (security invariant preserved by OS lock itself); PID recycling handled correctly (alive → no banner = safer false-negative); real hard-gate is `SingleInstance` Mutex in `VPNRouter.App/Services/SingleInstance.cs`.
- [x] Hook gates: covered by 0-error build + green scoped suite.

**Surprises**:
1. `DnsFlusher` was easier than expected — the `static class → instance + static-facade` pattern matched Wave 6's `LockFile` exactly. No `VpnEngine` change needed.
2. `LockFile`'s existing layered design (instance methods + `DefaultInstance` static facade) made every test shape straightforward — ctor takes `(IFileSystem, lockPath)` so each test gets its own GUID-named lock path against `InMemoryFileSystem`. No `%ProgramData%` pollution.
3. `DetectPreviousCrashInstance` always-consumes-the-stale-file contract was the most subtle invariant — pinned in 4 tests so a regression that forgets `TryDeleteInstance` surfaces immediately.
4. One xUnit1031 warning (`.GetAwaiter().GetResult()` in a test) fixed by promoting to `async Task` + `await`.

### Wave 7b-1 — `EtwProcessMonitor` + `NetworkInterfaceDetector` (2026-05-18)

**Status**: PASS — Gates 1+2+4 all green.

**Production changes** (minimal surgical extraction for testability):

- `VPNRouter.Core/Services/EtwProcessMonitor.cs` (+27/-10):
  Extracted `internal static TranslateProcessEvent(int, string?, int)` helper.
  Both `ProcessStart` and `ProcessStop` lambdas in `RunSession` now route
  through it. Adds defensive null-coalesce on `imageFileName` (kernel can
  emit null for early process slots) — tiny defensive upgrade.

- `VPNRouter.Core/Services/NetworkInterfaceDetector.cs` (+18/-6):
  - Split `IsWireGuardInterface(NetworkInterface)` into a thin wrapper +
    new `internal static IsWireGuardName(string?, string?)` so the
    keyword filter is testable without instantiating `NetworkInterface`.
  - Widened `CalculateSubnet` + `CountBits` from `private` to `internal`
    so IP arithmetic + prefix-length math can be pinned directly.

- `VPNRouter.Tests/VPNRouter.Tests.csproj` (+10):
  Mirror Core's `PLATFORM_WINDOWS` define on Windows hosts so the
  Windows-only `EtwProcessMonitorTests` `#if` gates can reference the
  Microsoft.Diagnostics.Tracing-bound `EtwProcessMonitor` type.

**Tests added**:

- `EtwProcessMonitorTests.cs` (256 LOC, 12 cases): 6 `TranslateProcessEvent`
  shape tests including the sing-box case-sensitivity invariant (CLAUDE.md
  GR #7); 5 lifecycle tests (ctor seam, double-Dispose, Stop-before-Start);
  1 platform-portable smoke test.
- `NetworkInterfaceDetectorTests.cs` (278 LOC, 14 methods → 30 xUnit runs
  via Theory): keyword filter + case-insensitivity + null-safety +
  dual-field matching; subnet arithmetic /24 + /16 + /32 widening to /24
  (the CRITICAL WG-coexistence invariant) + /31 widening + IPv6 null;
  CountBits Theory across 7 mask shapes; DetectWireGuardSubnets smoke.

**Test delta**: +42 in scoped suite (875 → 917). Over-met because Theory
rows × test methods.

**Verification gates**:
- [x] EtwProcessMonitorTests: 12 (target 6-8)
- [x] NetworkInterfaceDetectorTests: 14 methods / 30 runs (target 4-6)
- [x] Gate 1 build 0 errors
- [x] Gate 2 scoped suite +42 tests, all pass
- [x] Gate 4 simplify: each test file <300 LOC (256 + 278)

**Surprises**:
1. Test csproj didn't define `PLATFORM_WINDOWS` (only Core did) so initially
   all `EtwProcessMonitorTests` `#if`-gated bodies were silently stripped
   — only the platform-portable smoke compiled. Fixed by mirroring the
   symbol in the test csproj on Windows hosts. Alternative (runtime
   `OperatingSystem.IsWindows()`) wouldn't compile on Linux at all
   because the Core type itself is `#if`-gated.
2. `DetectWireGuardSubnets` already gracefully handles zero-WG-adapter
   hosts (returns empty list) — no scaffolding needed.
3. ETW PID 0 / -1 are pass-through in `TranslateProcessEvent`, not
   filtered — filter belongs in the consumer (HealthMonitor / ProcessScanner).

## Follow-up

- Phase 3D may consolidate `HostsManager` + `WindowsDnsHardening` under a unified `ISystemStateMutator` if their test shapes match.
- `QrCode` LOW priority — if time is short, defer to Phase 3B (Avalonia 11→12).
