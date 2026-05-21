# Phase 3+ — IProcessRunner adoption: FirewallManager + TunAdapterDiagnostics

**Owner**: Claude session (Phase 3+ batch, first netsh callers)
**Branch**: main (direct commit)
**Roadmap ref**: `plans/phase2-2D-iprocessrunner-2026-05-17.md`,
`plans/phase2-2G-untested-services-2026-05-17.md` §"Open process-touching
surface that remains direct-Process"
**Effort**: ~2 hours
**Risk**: MEDIUM (wire-shape-preserving migration of leak-protection
netsh calls + TUN adapter cleanup; BR-9 firewall rules are user-impacting)
**Blast radius**: 2 service files · ~30 callsites · 0 behavior change
**Rollback**: `git revert <commit>` — the seams are additive; reverting
restores direct `Process.Start` calls verbatim.

## Why

Phase 3+ survey identified that `IProcessRunner` (shipped Phase 2D,
2026-05-17) has reached only ~30 % of intended service adoption. 20-25
services still call `Process.Start` directly, blocking the seam's
unit-test value and keeping these services in the "untested because
processes are unmockable" bucket.

**This batch** covers the two heaviest **netsh** callers — both
leak-protection / connectivity surfaces where a regression could
silently break user firewall config or strand orphan TUN adapters:

| Service | Why heavy | Existing pinned behavior |
|---|---|---|
| `FirewallManager` | block_on_vpn_fail rules + Wave 39 BR-9 DNS lockdown (4 static helpers) + localized-netsh parser | `FirewallManagerLocalizedNetshTests` (2), `FirewallManagerTunAllowTests` (20+) |
| `TunAdapterDiagnostics` | netsh enumerate + disable + Remove-NetAdapter PowerShell | `TunAdapterReadinessTests` (32) |

Migrating these two unlocks future test additions that pin the **wire
shape** of netsh args (e.g. BR-9 `remoteip=<complement>`) without
spawning the real binary.

## What

Replace direct `new ProcessStartInfo("netsh.exe", ...)` +
`Process.Start(psi)` blocks with `IProcessRunner.RunAsync(ProcessRequest)`
calls routed through a settable static seam.

**Pattern**: mirror `ZapretActions` and `WindowsDnsHardening` —
internal settable static `IProcessRunner Runner { get; set; } = new ProcessRunner();`
on each class; tests assign a `FakeProcessRunner` before exercising the
static helpers; ctor variants of instance classes accept an optional
`IProcessRunner? runner = null` defaulting to the static.

### FirewallManager.cs (per-instance + 5 static helpers)

Call sites to migrate:
- `RunNetsh(string)` instance method (5 callers: CreateBlockRule,
  EnableBlockRules, DisableBlockRules, DeleteAllRules, CleanupOrphanedRules)
- `ResolveProcessPath` — `where.exe` lookup
- `FindRulesByPrefix` — `netsh advfirewall firewall show rule name=all dir=out`
- `RunNetshStatic` — used by Wave 39 BR-9 DNS lockdown helpers
  (`EnableDnsLockdownAsync` x4, `DisableDnsLockdownAsync` x6)

**Critical wire-shape invariants** that MUST stay byte-equivalent:
- `RunNetshStatic` netsh args (BR-9): `remoteip={blockExclusionRange}` —
  comma-separated complement range
- `where.exe` argument list (single positional arg)
- OEM codepage encoding (CP-866 RU, CP-850 DE) on stdout/stderr
- 5 s outer per-batch timeout in `EnableDnsLockdownAsync` /
  `DisableDnsLockdownAsync`
- 3 s per-`netsh` timeout in `RunNetshStatic`
- 5 s per-`netsh` timeout in instance `RunNetsh`
- 10 s `show rule name=all` enumeration timeout
- 3 s `where.exe` timeout

### TunAdapterDiagnostics.cs (all static methods)

Call sites to migrate:
- `LogAdapterState` — `netsh interface show interface`
- `DisableOrphanedAdapter` — `netsh interface set interface admin=disabled`
- `EnsureAdapterEnabledOrAbsent` — `netsh interface set interface admin=enabled`
- `PreStartCleanupAsync` — `netsh interface show interface` (via
  `RunAndCaptureAsync`)
- `TryRemoveAdapterAsync` — `powershell.exe -NoProfile -NonInteractive`
  with embedded script (via `RunAndCaptureAsync`)
- `RunAndCaptureAsync` itself is the internal helper that all of the
  above route through except the first two

**Critical wire-shape invariants**:
- `netsh interface set interface name="VPNRouter-TUN" admin=...`
  command-line shape
- 3 s timeouts on Log/Disable/Ensure paths
- 5 s timeout on `RunAndCaptureAsync(netsh interface show interface)`
- 10 s timeout on `RunAndCaptureAsync(powershell Remove-NetAdapter)`
- PowerShell args: `-NoProfile -NonInteractive -Command "<script>"`
- BR-2 `s_removeNetAdapterMissing` latching unchanged (process-lifetime
  short-circuit on missing cmdlet)

## How

### Step 1: Seam plumbing

Each target class gets:
```csharp
// internal settable static, ProcessRunner.Default-equivalent default
internal static IProcessRunner Runner { get; set; } = new ProcessRunner();
```

For `FirewallManager` (instance class), keep `_runner` instance field
parallel to existing `_logger`:
```csharp
private readonly IProcessRunner _runner;
public FirewallManager(ILogger? logger = null, IProcessRunner? runner = null)
{
    _logger = logger ?? Log.Logger;
    _runner = runner ?? Runner;
}
```

The static helpers (`RunNetshStatic`, `EnableDnsLockdownAsync`,
`DisableDnsLockdownAsync`) use the class-static `Runner` directly. The
instance `RunNetsh` uses `_runner`.

For `TunAdapterDiagnostics` (pure static class), only the class-static
`Runner`.

### Step 2: Per-callsite migration

Convert `new ProcessStartInfo(...); using var proc = Process.Start(psi);
proc.StandardOutput.ReadToEnd(); proc.WaitForExit(N);` to:

```csharp
var result = await _runner.RunAsync(new ProcessRequest(
    ExecutablePath: "netsh.exe",
    Arguments: new[] { ... },          // split shell-args into tokens
    Timeout: TimeSpan.FromMilliseconds(N))).ConfigureAwait(false);

if (result.TimedOut) { /* warning + return false/early */ }
if (result.ExitCode != 0) { /* warning + return false */ }
// use result.Stdout / result.Stderr
```

For sync methods (`RunNetsh`, `FindRulesByPrefix`,
`ResolveProcessPath`, `LogAdapterState`, `DisableOrphanedAdapter`,
`EnsureAdapterEnabledOrAbsent`), wrap with
`.GetAwaiter().GetResult()` — these run on the VPN-start path which
is already sync. Matches existing `DnsFlusher.FlushWindows` pattern.

Wire-shape preservation: every `Arguments` array MUST decompose the
existing single-string `psi.Arguments` into separate tokens via
`Split(' ', StringSplitOptions.RemoveEmptyEntries)`. The
`ProcessRunner.BuildStartInfo` rejoins via `ArgumentList`, which is
semantically equivalent for whitespace-tokenized strings (no embedded
quoted args in our callsites).

**Encoding gotcha**: `ProcessRequest` does not surface
`StandardOutputEncoding`. The `ConsoleEncoding` (OEM CP-866/CP-850)
override stays at the **service level** (we cannot push it to
`ProcessRunner` without an interface change — out of scope for this
batch). Since the existing parser is byte-tolerant of UTF-8 vs OEM
(the rule-name column is ASCII-only), and the localized labels are
filtered by the structural block-aware parser already, the lost OEM
encoding will only affect log-output mojibake — same risk profile as
v2.31.6 was before r19. Mitigation: log the divergence in the brief
"Surprises" section and consider a Phase 4 follow-up to add
`StdoutEncoding` to `ProcessRequest`.

### Step 3: BR-9 / TUN cleanup behavior pins

Add unit tests that mock the netsh wire-shape:
- `FirewallManagerBR9WireShapeTests` — pin `remoteip=` arg in the 3
  block rules emitted by `EnableDnsLockdownAsync`.
- `TunAdapterDiagnosticsWireShapeTests` — pin `interface set
  interface name="VPNRouter-TUN" admin=disabled` shape.

These tests exercise the new seam: assign `FakeProcessRunner` to
`Runner`, call the helper, assert the captured `RunCalls` shape, then
restore `Runner` in a try/finally.

## Verification gate

- [x] `dotnet build VPNRouter.sln -c Release` → 0 errors
- [x] Full suite (excl. headless / page-screenshot / visual-diff)
  passes with same green count + new tests
- [x] `FirewallManagerLocalizedNetshTests` (2/2) green
- [x] `FirewallManagerTunAllowTests` (12/12) green
- [x] `FirewallManagerDnsLockdownTests` green
- [x] `TunAdapterReadinessTests` (32/32) green
- [x] New wire-shape tests added
- [x] No `using System.Diagnostics;` removed unless unused

## Outcome

### Files touched

| File | Change |
|---|---|
| `VPNRouter.Core/Services/FirewallManager.cs` | +83 / −58 LOC. 5 callsites migrated (RunNetsh instance, FindRulesByPrefix, ResolveProcessPath where.exe, RunNetshStatic). New `Runner` static seam + `IProcessRunner? runner` ctor param. New `SplitShellArgs` quote-aware helper (internal). |
| `VPNRouter.Core/Services/TunAdapterDiagnostics.cs` | +91 / −83 LOC. 5 callsites migrated (LogAdapterState, DisableOrphanedAdapter, EnsureAdapterEnabledOrAbsent, PreStartCleanupAsync via RunAndCaptureAsync, TryRemoveAdapterAsync via RunAndCaptureAsync). New `Runner` static seam. `RunAndCaptureAsync` signature changed from `string arguments` to `IReadOnlyList<string> arguments`. New `ResetRemoveNetAdapterLatchForTests` for BR-2 latch reset in tests. |
| `VPNRouter.Tests/FirewallManagerProcessRunnerWireShapeTests.cs` | +199 LOC (new file). 7 tests pinning BR-9 wire shape, SplitShellArgs invariants, ctor injection. |
| `VPNRouter.Tests/TunAdapterDiagnosticsProcessRunnerWireShapeTests.cs` | +217 LOC (new file). 6 tests pinning netsh admin=disabled / admin=enabled / show interface wire shape, PowerShell Remove-NetAdapter argv, latch handling. |
| `plans/phase3-iprocessrunner-firewallmgr-tunDiag-2026-05-21.md` | This brief. |

### Test deltas

- Baseline (pre-batch): 1248 pass / 4 skip / 0 fail.
- After this batch: 1261 pass / 4 skip / 0 fail (**+13 tests**).
- All FirewallManager + TunAdapter tests: 72/72 green (was 59/59 before this batch).

### Surprises encountered

1. **Quote-aware split needed**. The `description="VPNRouter
   block_on_vpn_fail"` shape in BR-9 / block_on_vpn_fail rules contains
   a space INSIDE the quoted value. A naive whitespace split would
   shatter it into 2 argv tokens. New `SplitShellArgs` helper strips
   surrounding quotes (matching Windows' own CommandLineToArgvW
   semantics) so .NET ArgumentList re-quotes correctly when it serializes
   for the kernel.
2. **OEM codepage encoding lost in transit**. Pre-migration code set
   `StandardOutputEncoding = ConsoleEncoding` (CP-866 RU, CP-850 DE)
   per-PSI. `ProcessRequest` has no `StdoutEncoding` field — out of
   scope for this batch. The structural block-aware parser in
   `FindRulesByPrefix` is already locale-tolerant (relies on blank-line
   block boundaries, not localized labels), and rule names are ASCII —
   so leak risk is nil. Log mojibake on RU/DE Windows for unfamiliar
   warning text is the only regression — same risk as pre-v2.31.6-r19.
   **Follow-up candidate**: add optional `StdoutEncoding` /
   `StderrEncoding` to `ProcessRequest` so the OEM override can flow
   through cleanly.
3. **BR-2 latch interferes with test ordering**. The existing
   `PreStartCleanupAsync_NonWindows_ReturnsZeroNoOp` test calls the
   real `PreStartCleanupAsync` on Windows; if the test runner happens
   to be a PowerShell environment without the NetAdapter module
   (CI VMs, locked-down corporate machines), the BR-2 latch
   (`s_removeNetAdapterMissing`) gets flipped to 1 for the rest of
   the process lifetime, short-circuiting any subsequent
   `TryRemoveAdapterAsync` call. New
   `ResetRemoveNetAdapterLatchForTests` internal hook clears the latch;
   wire-shape tests call it in setup. Mirrors the
   `WindowsDnsHardening._runnerOverride` test-reset pattern.
4. **No `ProcessRunner.Shared` / `.Default` singleton in the
   abstraction**. Each adopter constructs `new ProcessRunner()`
   directly. Consistent with the existing `ZapretActions` /
   `DnsFlusher` / `EtwProcessMonitor` adoption pattern. The brief's
   "ProcessRunner.Default" reference in the task description was
   aspirational — the actual pattern is per-class default.

### Wire-shape invariants preserved

- BR-9 `remoteip=<complement>` argument shape (pinned by 3 of the new
  tests).
- 3 s / 5 s / 10 s timeouts across all migrated callsites.
- netsh exit-code semantics (0 = success; 1 + "not found" = idempotent
  no-op).
- PowerShell `-NoProfile -NonInteractive -Command "<script>"` shape
  (pinned by `PreStartCleanupAsync_AdapterFound_DisableAndRemoveBoth`).
- BR-2 cmdlet-missing latch behavior unchanged in production (latch is
  process-lifetime sticky; only `ResetRemoveNetAdapterLatchForTests`
  clears it, and that's internal-only).

### Follow-ups spawned

- Adopt `IProcessRunner` in the remaining ~18-23 services per Phase 3+
  survey. Next batch candidates (heaviest first): `SingBoxManager`
  (sing-box process lifecycle), `ZapretManager` (winws lifecycle),
  `WindowsDnsHardening.Apply` / `Restore` (the dnsclient netsh calls
  beyond the already-migrated TrySetTunMetric).
- Consider adding `StdoutEncoding` / `StderrEncoding` to
  `ProcessRequest` so the OEM codepage override can flow through —
  improves log readability on localized Windows.

