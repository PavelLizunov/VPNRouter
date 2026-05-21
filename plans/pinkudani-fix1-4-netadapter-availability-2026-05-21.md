# PinkuDani Fix #1 + #4 — NetAdapter module availability + netsh-disable fallback

**Owner**: Claude session (PinkuDani class hardening, brat-class follow-up)
**Branch**: main (direct commit)
**User reference**: PinkuDani 2026-05-21, Windows 10 LTSC (RU-RU) without
`NetAdapter` PowerShell module
**Effort**: ~2 hours
**Risk**: LOW (additive: cache layer + new helper; no changes to existing
wire-shape; BR-2 reactive latch retained as belt-and-suspenders)
**Blast radius**: 1 service file (`TunAdapterDiagnostics.cs`) · 1 new test
file (`TunAdapterDiagnosticsNetAdapterAvailabilityTests.cs`) · ~6 new tests
**Rollback**: `git revert <commit>` — additive only; the `_netAdapterModuleAvailable`
Lazy can be left in (idle until next start). No existing call sites change.

## Why

PinkuDani's 2026-05-21 log (`Z:\PinkuDani\vpnrouter20260521_002.log`)
revealed a Windows 10 LTSC install **without** the `NetAdapter` PowerShell
module. Every call to `Remove-NetAdapter` exits with
`CommandNotFoundException` after ~1.5-2 s of PowerShell startup cost.
Five callsites per VPN connect cycle fire this PowerShell:

- `StartupPipeline.ExecuteAsync` → `PreStartCleanupAsync` (enum + fallback)
- `SingBoxManager.LaunchProcess` → `PreStartCleanupAsync` (enum + fallback)
- `SingBoxManager.StopInternal.early` → `TryRemoveAdapterAsync` direct
- `SingBoxManager.StopInternal.killed` → `TryRemoveAdapterAsync` direct
- `SingBoxManager.OnProcessExited` → `TryRemoveAdapterAsync` direct

Log trace (lines 78-85, 194-200, 208-214) shows each call burning ~1.5-4 s
before failing. Cumulative cost stretched VM Start past 30 s →
`[ERR] [VM] Start timed out after 30s` → forced Stop on a connecting VPN.
The mojibake CP-866 stderr means BR-2's reactive latch (which looks for
the **literal UTF-16 strings** `не распознано` / `nicht erkannt` /
`is not recognized` in `Stderr`) **never fires** on Russian Windows
because OEM CP-866 garbles `не распознано` to `­Ґ а бЇ®§­ ­®` and the
substring match fails. Latch stays at 0; every callsite re-spawns
PowerShell every cycle.

## What

Two complementary defences, both fully additive (BR-2 reactive latch kept
intact as belt-and-suspenders):

### Fix #1: NetAdapter module pre-flight check + Lazy cache

Add a `Lazy<bool>` static field in `TunAdapterDiagnostics` that, on first
call to `TryRemoveAdapterAsync`, runs `powershell.exe -NoProfile
-NonInteractive -Command "Get-Module NetAdapter -ListAvailable |
Measure-Object | Select -ExpandProperty Count"`. Parses the stdout — `>0`
means module present, else absent. Caches the result for the process
lifetime.

Once `_netAdapterModuleAvailable.Value == false`, **every** subsequent
`TryRemoveAdapterAsync` call returns immediately with a Debug-level log
("skipped — NetAdapter module unavailable"). The first call to report
absence logs a single user-actionable **Information** ("PowerShell
NetAdapter module not available — falling back to netsh disable for TUN
cleanup. For reliable cleanup install RSAT-NetAdapter or upgrade to a
Pro/Enterprise SKU.") instead of WARN to avoid being mistaken for a
problem in normal logs.

### Fix #4: netsh-based orphan removal fallback

When `_netAdapterModuleAvailable.Value == false` AND enumeration finds a
stale TUN adapter, `PreStartCleanupAsync` calls a new helper
`TryDisableAdapterViaNetshAsync` instead of skipping. The helper is
`netsh interface set interface name=<NAME> admin=disabled` — already
present in `DisableOrphanedAdapter`, but exposed as an awaitable
internal helper so `PreStartCleanupAsync` can dispatch it after the
Lazy guard.

netsh **cannot delete** the device record (only `Remove-NetAdapter` can),
but disable releases the kernel handle and prevents the wintun
"create when file already exists" FATAL on the next sing-box launch.

## Cache invariants

- Cache is **lazy** — Get-Module never runs unless a `TryRemoveAdapterAsync`
  fires. Users who never trigger TUN cleanup pay nothing.
- Cache is **process-lifetime** — once `_netAdapterModuleAvailable.Value`
  resolves, it stays that way. If user installs RSAT mid-session, restart
  VPNRouter. (Spec-required: "Don't invalidate mid-session.")
- Cache **does not invalidate on Remove-NetAdapter failure** — module
  available + first call fails (e.g. permissions, adapter busy) → cache
  stays "available", next call still attempts. Only the `Get-Module` probe
  itself sets the cache.
- BR-2 reactive latch stays intact — it's a second line of defence for
  the case where Get-Module returned a positive count but the cmdlet
  itself somehow goes missing later.

## New tests

`VPNRouter.Tests/TunAdapterDiagnosticsNetAdapterAvailabilityTests.cs`:

1. `NetAdapterAvailable_TrueResult_TryRemoveCallsPowerShell` — Get-Module
   stub returns `"1"`, verify second PowerShell call (the actual
   Remove-NetAdapter) fires.
2. `NetAdapterAvailable_FalseResult_TryRemoveSkipsPowerShell` —
   Get-Module returns `"0"`, verify only ONE PowerShell call (the probe
   itself), no Remove-NetAdapter spawn.
3. `NetAdapterAvailable_CachedAcrossCalls` — 5 sequential
   TryRemoveAdapterAsync calls + Get-Module returns `"1"` once → only
   ONE Get-Module probe fired (cache works).
4. `NetAdapterUnavailable_PreStartCleanup_FallsBackToNetshDisable` —
   orphan TUN found via netsh enumeration + Get-Module returns `"0"` →
   netsh disable invocation captured, no Remove-NetAdapter spawn.
5. `NetAdapterUnavailable_FirstCall_LogsActionableInfo` — Serilog test
   sink captures the user-actionable INF message.
6. `NetAdapterAvailable_Cache_DoesNotInvalidateOnRemoveFailure` — module
   available BUT first Remove-NetAdapter call fails (exit 5, "Access
   denied") → next TryRemoveAdapterAsync still attempts (cache stays
   "available", doesn't downgrade).

## Verification gates

1. `dotnet build VPNRouter.sln -c Release` → 0 errors.
2. `dotnet test ... --filter "FullyQualifiedName!~PageScreenshotTests&
   FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
   → 1304+ pass (baseline `fe56f5a`).
3. All 6 existing `TunAdapterDiagnosticsProcessRunnerWireShapeTests` +
   all 32 `TunAdapterReadinessTests` stay green.
4. New tests (6) all pass.

## Public surface for Fix #3 agent

The new helper exposed for SingBoxManager's restart path to call:

```csharp
namespace VPNRouter.Core.Services;

public static class TunAdapterDiagnostics
{
    /// <summary>
    /// PinkuDani Fix #4 (2026-05-21): netsh-based orphan disable
    /// fallback for environments where PowerShell Remove-NetAdapter is
    /// unavailable (Win10 LTSC / stripped installs).
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static Task<bool> TryDisableAdapterViaNetshAsync(
        ILogger? logger,
        string adapterName,
        string context);

    /// <summary>
    /// PinkuDani Fix #1 (2026-05-21): probe NetAdapter PowerShell module
    /// availability. Cached for process lifetime. Returns true if
    /// Remove-NetAdapter is expected to work, false if it will fail with
    /// CommandNotFoundException.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsNetAdapterModuleAvailable();
}
```

Fix #3's `SingBoxManager` recovery path can call
`IsNetAdapterModuleAvailable()` to decide whether to schedule the
PowerShell removal or jump straight to `TryDisableAdapterViaNetshAsync`,
shaving ~600-800 ms per restart attempt on PinkuDani-class machines.

## Coordination notes

- Parallel agent: Fix #2 — VM Start timeout cancellation-aware (App
  layer). No conflict; different files.
- Sequential agent: Fix #3 — SingBoxManager TUN reconfig crash recovery.
  Picks up the public surface above.

## Outcome

**Status**: shipped 2026-05-21. Direct commit on `main`.

### Callsites changed

5 callsites benefit via the central `TryRemoveAdapterAsync` seam:

- `StartupPipeline.ExecuteAsync` (via `PreStartCleanupAsync`)
- `SingBoxManager.LaunchProcess` (via `PreStartCleanupAsync`)
- `SingBoxManager.StopInternal.early` (direct `TryRemoveAdapterAsync`)
- `SingBoxManager.StopInternal.killed` (direct `TryRemoveAdapterAsync`)
- `SingBoxManager.OnProcessExited` (direct `TryRemoveAdapterAsync`)

Plus `PreStartCleanupAsync` itself now branches on availability:

- Module **available** → unchanged path (DisableOrphanedAdapter + Remove-NetAdapter).
- Module **missing** → `TryDisableAdapterViaNetshAsync` only (kernel handle released, device record remains).

### Build + test

- `dotnet build VPNRouter.sln -c Release` → 0 errors.
- `dotnet test ... --filter "FullyQualifiedName!~PageScreenshotTests&
  FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
  → **1310 passed, 0 failed, 4 skipped** (vs `fe56f5a` baseline 1304 →
  added 6 new tests, all green).
- All 6 existing `TunAdapterDiagnosticsProcessRunnerWireShapeTests` +
  32 `TunAdapterReadinessTests` stay green. One existing test
  (`PreStartCleanupAsync_AdapterFound_DisableAndRemoveBoth`) tightened to
  filter PS calls by Remove-NetAdapter shape (was overbroad: assumed first
  PS call = Remove-NetAdapter; new Get-Module probe is now first). Pin
  on Remove-NetAdapter wire shape itself unchanged.

### Get-Module probe time measurement (test environment)

5 cold-start runs of `powershell.exe -NoProfile -NonInteractive -Command
"Get-Module NetAdapter -ListAvailable | Measure-Object | Select -ExpandProperty Count"`:

```
times=362ms,331ms,374ms,343ms,336ms avg=349ms max=374ms
```

Well under the 500 ms refuse-to-proceed threshold. One-time per process
lifetime cost.

### BR-2 cache from r6 — was it sufficient?

**Existed but insufficient.** BR-2 latch was already in place from
Wave 39 (brat 2026-05-19) and looked for `is not recognized` /
`не распознано` / `nicht erkannt` in **stderr text**. On PinkuDani's
Russian Windows 10 LTSC, CP-866 OEM encoding garbled "не распознано"
into `­Ґ а бЇ®§­ ­®` — the substring match never fired, latch stayed
at 0, every callsite re-spawned PowerShell.

Fix #1 supersedes the reactive approach with a proactive Lazy probe
that parses an **integer count**, not a localised error string —
locale-independent by construction. BR-2 latch retained as
belt-and-suspenders for the edge case where Get-Module reports
"available" but a Remove-NetAdapter call still hits CommandNotFoundException
(rare; would suggest module manifest present but cmdlet damaged).

### Public surface for Fix #3 agent

```csharp
namespace VPNRouter.Core.Services;

public static class TunAdapterDiagnostics
{
    [SupportedOSPlatform("windows")]
    internal static bool IsNetAdapterModuleAvailable();
    // Returns the cached probe result. Lazy resolves on first call.
    // Use this to gate PowerShell-based Remove-NetAdapter scheduling
    // — if false, jump straight to TryDisableAdapterViaNetshAsync.

    [SupportedOSPlatform("windows")]
    internal static Task<bool> TryDisableAdapterViaNetshAsync(
        ILogger? logger,
        string adapterName,
        string context);
    // Awaitable netsh admin=disabled. Returns true on exit 0 or
    // "not found" idempotent path, false on real failure.
    // Cheaper than full Disable+Remove cycle (~5-50 ms vs ~600-800 ms).
}
```

Fix #3 (SingBoxManager TUN reconfig crash recovery) usage pattern:

```csharp
if (TunAdapterDiagnostics.IsNetAdapterModuleAvailable())
{
    // Full cleanup: disable + Remove-NetAdapter.
    TunAdapterDiagnostics.DisableOrphanedAdapter(logger, name, ctx);
    await TunAdapterDiagnostics.TryRemoveAdapterAsync(logger, name, ctx);
}
else
{
    // Fast netsh-only path for PinkuDani-class Windows installs.
    await TunAdapterDiagnostics.TryDisableAdapterViaNetshAsync(logger, name, ctx);
}
```

### Unverified assumption flagged

netsh `interface set ... admin=disabled` is documented to release the
kernel handle held by the wintun driver — but kernel-test would require
a Win10 LTSC machine without the NetAdapter module to confirm sing-box's
next `WintunCreateAdapter` no longer hits ERROR_FILE_EXISTS after
netsh-only cleanup. Field validation by PinkuDani when they next
auto-update will close this loop.

### Commit

`66e1407` — `fix(tundiag): cache NetAdapter availability + netsh fallback`.
