# Phase 4 prep — TunAdapterDiagnostics happy-path tests (Task #36-B)

**Date**: 2026-05-21
**Branch**: `main`
**Scope**: Test-only — no production code changes.

## Why

Phase 3+ Fix #1 (PinkuDani) and Fix #4 (PinkuDani) landed the
`IProcessRunner` seam, lazy NetAdapter probe, and the
`TryDisableAdapterViaNetshAsync` fallback inside `TunAdapterDiagnostics`
(commits `66e1407`, `38df0bf`). The two sibling test suites
(`TunAdapterDiagnosticsProcessRunnerWireShapeTests`,
`TunAdapterDiagnosticsNetAdapterAvailabilityTests`) pin the per-call
argv shape and the module-availability cache invariants but stop short
of the end-to-end "orphan found → cleanup succeeds, count returned"
contract that production callers (`VpnEngine` pre-start gate,
`SingBoxManager` auto-restart paths) actually consume.

This file adds three happy-path tests at the
`PreStartCleanupAsync(ILogger?, string)` orchestrator level so the
high-level behaviour is locked down ahead of Phase 4 work.

## What

`VPNRouter.Tests/TunAdapterDiagnosticsHappyPathTests.cs` — one sealed
class, three `[Fact]`s, plain xUnit (no Avalonia headless needed):

1. **`PreStartCleanupAsync_OrphanFound_ModuleAvailable_RemoveNetAdapterFires`**
   — `Get-Module NetAdapter` Lazy pre-set to `true`; netsh enumeration
   stubbed to return a single `VPNRouter-TUN` row; netsh disable + PS
   Remove-NetAdapter both return success. Pins: returned count = 1,
   enumeration fired exactly once, netsh disable carries
   `name=VPNRouter-TUN`, PS argv = `[-NoProfile, -NonInteractive,
   -Command, "<script with 'VPNRouter-TUN'>"]`.

2. **`PreStartCleanupAsync_OrphanFound_ModuleUnavailable_NetshFallbackFires`**
   — Lazy pre-set to `false`; same enumeration stub. Pins: returned
   count = 1, NO PowerShell call in `RunCalls`, netsh disable argv shape
   = `[interface, set, interface, name=VPNRouter-TUN, admin=disabled]`.
   This is the PinkuDani Win10 LTSC path (Fix #4).

3. **`PreStartCleanupAsync_NoOrphans_ModuleUnavailable_SkipsPowerShellRemoval`**
   — Enumeration returns Ethernet/Wi-Fi only (no `VPNRouter-TUN` /
   `sing-box-tun`); fallback netsh-disable stubbed to return exit 5
   ("Access is denied") so the defence-in-depth direct-by-name pass
   gets a `false` from `TryDisableAdapterViaNetshAsync` and the count
   stays at 0. Pins: returned count = 0, enumeration fired exactly
   once, NO PowerShell call at all.

## What's NOT

- No production code change. `TunAdapterDiagnostics.cs`,
  `IProcessRunner.cs`, the `Runner` static seam — none of them edited.
- No interface extraction or refactor. Task #36-A (parallel) handles
  the `IWindowsDnsHardening` extraction in a different file.

## How it's wired

- `Runner` static seam from `TunAdapterDiagnostics.cs` is swapped in a
  try/finally helper (`WithFakeAsync`) per the sibling test pattern.
- `SetNetAdapterModuleAvailableForTests(bool)` pre-sets the Lazy so
  the production `Get-Module` probe never spawns a real PowerShell.
- `ResetRemoveNetAdapterLatchForTests()` runs both before AND after
  each test to guarantee isolation from any sibling test that may have
  observed a CommandNotFoundException via the real runner.
- `Assert.SkipUnless(OperatingSystem.IsWindows(), ...)` gates each
  test as the FIRST statement, matching the post-`ddc2399` pattern.
  Linux CI skips silently rather than failing on the `[SupportedOSPlatform]`
  early-return baseline of 0.

## Verification gates

- `dotnet build VPNRouter.sln -c Release` → 0 errors, 274 warnings
  (all pre-existing xUnit1051 noise from RuleSetCacheManagerTests
  and VlessDeepVerifierBehaviourTests).
- `dotnet test ... --filter "FullyQualifiedName~TunAdapterDiagnosticsHappyPathTests"`
  → 3/3 passed, 0 failed, 0 skipped (Windows host).
- `dotnet test ... --filter "FullyQualifiedName~TunAdapter"` (full
  TunAdapter sibling-suite regression) → 44/44 passed.
- Full suite minus GUI/screenshot:
  `dotnet test ... --filter "FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
  → 1322 passed, 4 skipped, 0 failed (1319 baseline + 3 new = 1322).

## Linux CI

The three tests skip via `Assert.SkipUnless(OperatingSystem.IsWindows(), ...)`
as their first statement. On Linux runners the skips don't fail the
build, and the assertion machinery never fires the netsh / PS expectations
that would otherwise mismatch against the early-return-0 baseline.
