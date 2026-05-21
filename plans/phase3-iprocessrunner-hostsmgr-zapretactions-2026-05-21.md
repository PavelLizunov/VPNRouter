# Phase 3+ — IProcessRunner adoption: HostsManager + ZapretActions

**Owner**: Claude session (Phase 3+ batch, second leaf services)
**Branch**: main (direct commit)
**Predecessor brief**: `plans/phase3-iprocessrunner-firewallmgr-tunDiag-2026-05-21.md`
(FirewallManager + TunAdapterDiagnostics, commit `1242b9e`)
**Effort**: ~1 hour
**Risk**: LOW (single Process.Start callsite in HostsManager — ipconfig flushdns;
ZapretActions remaining callsites are out-of-scope due to UseShellExecute/UAC
incompatibility with IProcessRunner)
**Blast radius**: 1 service file · 1 callsite · 0 behavior change
**Rollback**: `git revert <commit>` — the seam is additive; reverting restores
direct `Process.Start` in `HostsManager.FlushDns` verbatim.

## Why

Predecessor batch (`1242b9e`) migrated the two heaviest **netsh** callers.
Continuing the Phase 3+ adoption sweep, this batch covers the next two
candidates flagged by the Phase 3+ survey: `HostsManager` (1 callsite —
`ipconfig /flushdns` after every hosts mutation) and `ZapretActions`
(declared "leaf service" but already 5/8 callsites migrated in Phase 2G
sub-wave 7b-2).

**Why HostsManager matters**: a regression in `FlushDns` would silently
fail to refresh the Windows DNS cache after the Discord-voice hosts
entries are added/removed, leaving the user with stale resolution for
minutes-to-hours until the OS-level cache TTL expires. Pinning the
ipconfig wire shape via the IProcessRunner seam protects that path.

**Why ZapretActions is no-op**: the 3 remaining Process.Start callsites
(`OpenHostsEditHelpers`, `RunTests`, `OpenServiceMenu`) all use
`UseShellExecute=true` (GUI launchers + UAC elevation via `Verb="runas"`),
which `ProcessRunner.cs` deliberately does not support — its security
contract hard-wires `UseShellExecute=false` to prevent shell injection.
The Phase 2G sub-wave 7b-2 outcome doc (`plans/phase2-2G-untested-services-2026-05-17.md`
§"Out-of-scope") already flagged these as needing either an IProcessRunner
API extension (`UseShellExecute`/`Verb` fields) or special UAC handling.
**Neither is in scope for this batch.**

## What

### A. HostsManager.cs — 1 callsite migrated

- `FlushDns(ILogger?)` — converted from `static void` to instance `private
  void` so it can reach the new `_runner` field; routes `ipconfig
  /flushdns` through `IProcessRunner.RunAsync` with the existing 5s
  timeout preserved. The legacy `using var proc = Process.Start(psi);
  proc?.WaitForExit(5000)` pattern (v2.20.2 handle-leak fix) is preserved
  by transitive ownership — `ProcessRunner.RunAsync` owns its `Process`
  via `using` internally.
- Static `Runner` seam added (mirrors `FirewallManager.Runner`) so future
  static-call-site needs can swap in a fake without touching every
  ctor.
- Ctor extended with optional `IProcessRunner? runner = null` parameter
  (4th positional arg) — defaults to the static `Runner`. Backward
  compatible: existing `new HostsManager(fs, FakeHostsPath)` callsites in
  tests continue to work without modification.

### B. ZapretActions.cs — no migration (already done in Phase 2G)

Pre-existing migrations (commit history per file comments lines 24-29):
`RunSc`, `IsServiceRunning`, `IsAnyServiceMatching`, `ServiceExists`,
`RunNetsh`. All routed through the existing `_processRunner` static seam.

Remaining direct-`Process.Start` callsites (out-of-scope per
`plans/phase2-2G-untested-services-2026-05-17.md`):

| Callsite | What | Why not migrated |
|---|---|---|
| `OpenHostsEditHelpers` | `notepad <path>` + `explorer /select,<path>` GUI launch | `UseShellExecute=true` — IProcessRunner forbids |
| `RunTests` | `powershell -NoProfile -ExecutionPolicy Bypass -File <ps1>` | `UseShellExecute=true` (visible PowerShell window for user) — IProcessRunner forbids |
| `OpenServiceMenu` | `cmd.exe /k <service.bat>` with `Verb="runas"` | UAC elevation — `Verb` ignored under `UseShellExecute=false` per `ProcessRunner.cs` security notes |
| `ClearDiscordCacheAsync` | `Process.GetProcessesByName + Kill(entireProcessTree)` | No IProcessRunner.Kill seam — would need API extension |

These remain in the Phase 4 backlog. The brief at
`plans/phase2-2G-untested-services-2026-05-17.md` §"Out-of-scope (Phase
2G follow-up flagged in brief)" tracks them as a single follow-up bucket.

### C. New unit tests

- `VPNRouter.Tests/HostsManagerProcessRunnerWireShapeTests.cs` — 5 tests
  pinning the ipconfig argv shape (`{ "/flushdns" }`), the 5s timeout,
  symmetric Install/Uninstall flush calls, ipconfig-timeout-tolerance
  (mutation still succeeds even if DNS flush hangs), and the no-op
  short-circuit (already-installed path doesn't re-flush).

No new tests added for `ZapretActions` — existing
`VPNRouter.Tests/ZapretActionsTests.cs` (13 tests, all green) covers the
already-migrated callsites at the wire-shape level (`IsServiceRunning`,
`ServiceExists`, `IsAnyServiceMatching`, `RunSc`, `RunNetsh` arg-list
shape pins).

## How

### Step 1: Seam plumbing (HostsManager)

```csharp
// internal static seam, mirroring FirewallManager.Runner
internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

// per-instance field for ctor injection
private readonly IProcessRunner _runner;

// ctor: appended optional parameter, backward compatible
public HostsManager(IFileSystem? fileSystem = null, string? hostsPath = null,
                    IHttpClient? http = null, IProcessRunner? runner = null)
{
    _fs = fileSystem ?? new RealFileSystem();
    _hostsPath = hostsPath ?? HostsPath;
    _http = http ?? PolicyHttpClient.Shared;
    _runner = runner ?? Runner;
}
```

### Step 2: FlushDns migration

Converted from `static void FlushDns(ILogger?)` to instance `private void
FlushDns(ILogger?)` so the helper can reach `_runner`. All 4 call sites
(`InstallInstance`, `UninstallInstance`, `InstallFlowsealInstanceAsync`,
`UninstallFlowsealInstance`) were already calling `FlushDns(logger)` on
the instance — only the declaration's `static` modifier had to drop.

```csharp
var result = _runner.RunAsync(new ProcessRequest(
    ExecutablePath: "ipconfig",
    Arguments: new[] { "/flushdns" },
    Timeout: TimeSpan.FromMilliseconds(5000))).GetAwaiter().GetResult();

if (result.TimedOut) { logger?.Warning(...); return; }
logger?.Debug("[Hosts] DNS cache flushed");
```

Sync `.GetAwaiter().GetResult()` keeps the existing sync call sites
(`InstallInstance`, `UninstallInstance`) sync — matches the
`FirewallManager.RunNetsh` pattern.

### Step 3: Wire-shape tests

Tests assign a `FakeProcessRunner` via the new ctor parameter (not
through the static `Runner` seam — per-instance is cleaner here since
the manager is instance-only). Per-test setup creates a fresh
`InMemoryFileSystem` + `FakeProcessRunner` pair, exercises
`Install/UninstallInstance`, asserts on `fake.RunCalls`.

The `Install_WhenIpconfigTimesOut_StillReportsSuccess` test is the
load-bearing one: it pins that a hung ipconfig must not bubble up as
install failure (legacy code swallowed the timeout silently).

## Verification gate

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors
- [x] Core builds clean: `dotnet build VPNRouter.Core ...` → 0 errors
- [x] Tests project builds clean: `dotnet build VPNRouter.Tests ...` → 0 errors
- [x] HostsManagerTests + HostsManagerProcessRunnerWireShapeTests +
  ZapretActionsTests all green: **29/29 pass**
- [x] Full suite (excl. headless / page-screenshot / visual-diff):
  **1266 pass / 4 skip / 0 fail** (+5 vs 1261 baseline)
- [x] Wire-shape preserved: `ipconfig /flushdns` argv, 5s timeout,
  silent-on-timeout semantics

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/HostsManager.cs` | +33 / −15 LOC. 1 callsite migrated (FlushDns). New `Runner` static seam + `IProcessRunner? runner` ctor parameter. `FlushDns` converted from `static` to instance method (so it can reach `_runner`). |
| `VPNRouter.Tests/HostsManagerProcessRunnerWireShapeTests.cs` | +160 LOC (new file). 5 tests pinning ipconfig wire shape + ctor injection + timeout-tolerance. |
| `VPNRouter.Core/Services/ZapretActions.cs` | UNCHANGED — no callsites eligible for IProcessRunner migration remain (3 remaining direct-Process callsites need UseShellExecute / Verb support, out of scope). |
| `VPNRouter.Tests/ZapretActionsTests.cs` | UNCHANGED — 13/13 existing tests still cover the already-migrated surface. |
| `plans/phase3-iprocessrunner-hostsmgr-zapretactions-2026-05-21.md` | This brief. |

### Test deltas

- Baseline (predecessor batch `1242b9e`): 1261 pass / 4 skip / 0 fail.
- After this batch: **1266 pass / 4 skip / 0 fail** (**+5 tests**).
- HostsManager + Zapret-related suites: 29/29 green (was 24/24 pre-batch).

### Surprises encountered

1. **ZapretActions was a no-op migration target**. The brief's task
   description listed both files in symmetry with the predecessor batch,
   but Phase 2G sub-wave 7b-2 (commit history reflected in source-file
   comments at lines 21-29 of `ZapretActions.cs`) had already migrated
   every IProcessRunner-eligible callsite. The 3 remaining Process.Start
   call sites (`OpenHostsEditHelpers`, `RunTests`, `OpenServiceMenu`)
   are blocked by the IProcessRunner contract's hard-wired
   `UseShellExecute=false`. Migrating them requires an API extension
   that wasn't in this batch's scope.

2. **FlushDns had to drop `static`**. The legacy `FlushDns` was a
   `static void` that consumed only the `ILogger?` parameter and used
   `System.Diagnostics.ProcessStartInfo` directly. Routing through the
   per-instance `_runner` field requires instance access, so the
   modifier had to drop. All 4 callers were already calling
   `FlushDns(logger)` on the instance — no callsite churn.

3. **`Process.Start handle-leak fix (v2.20.2) transfers automatically**.
   The original code used `using var proc = Process.Start(psi); proc?.WaitForExit(5000)`
   specifically to avoid leaking native process handles on timeout (a
   v2.20.2 lesson). `ProcessRunner.RunAsync` owns its `Process` via
   `using` internally (per ProcessRunner.cs line 66), so the leak
   mitigation transfers automatically — no special handling needed.

4. **No `StdoutEncoding` issue here**. Unlike `FirewallManager.RunNetsh`
   on RU/DE Windows, `ipconfig /flushdns` emits only ASCII English
   strings ("Windows IP Configuration\r\nSuccessfully flushed..."), so
   the lost OEM-encoding override identified in the predecessor brief
   surprises §2 is a non-issue here.

### Wire-shape invariants preserved

- `ipconfig /flushdns` argv shape: single `"/flushdns"` token (pinned by
  4 of the 5 new tests).
- 5s timeout preserved.
- Timeout-tolerance: install/uninstall report success even if ipconfig
  hangs (pinned by `Install_WhenIpconfigTimesOut_StillReportsSuccess`).
- Native handle disposal: transferred to `ProcessRunner.RunAsync` via
  internal `using` (no per-callsite change needed).
- Idempotency short-circuit: `Already installed` path doesn't invoke
  ipconfig (pinned by `Install_AlreadyInstalled_DoesNotInvokeIpconfig`).

### Follow-ups spawned

- **ZapretActions 3-of-3 remaining callsites** (`OpenHostsEditHelpers`,
  `RunTests`, `OpenServiceMenu`). Either extend `ProcessRequest` with
  `UseShellExecute=false` (default) / `bool ShellExecute = false` /
  `string? Verb = null` so the migrate-or-not decision becomes a
  config-flag question, or keep these special-cased per the security
  contract. Tracked in `plans/phase2-2G-untested-services-2026-05-17.md`
  §"Out-of-scope".
- `ClearDiscordCacheAsync` uses `Process.GetProcessesByName + Kill` — no
  `IProcessRunner.KillByName` seam. Would need API extension.
- Adopt `IProcessRunner` in the remaining ~16-20 services per the
  Phase 3+ survey. Next batch candidates (heaviest first): long-lived
  spawn audit is running in parallel (per task description) — pick from
  whatever that audit doesn't claim. Likely targets: `WindowsDnsHardening.Apply`
  / `Restore` netsh calls beyond `TrySetTunMetric`, `EtwProcessMonitor`
  diagnostic-helper paths, `UpdateChecker` PowerShell-helper invocations.
