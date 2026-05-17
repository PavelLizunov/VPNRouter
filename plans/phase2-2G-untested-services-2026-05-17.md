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
*(filled by agent)*

## Follow-up

- Phase 3D may consolidate `HostsManager` + `WindowsDnsHardening` under a unified `ISystemStateMutator` if their test shapes match.
- `QrCode` LOW priority — if time is short, defer to Phase 3B (Avalonia 11→12).
