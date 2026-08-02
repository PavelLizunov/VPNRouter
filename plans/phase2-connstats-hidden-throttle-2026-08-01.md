# Phase 2 — ConnStats hidden/minimized throttle

**Owner**: Qwen Code via Codex
**Branch**: qwen/connstats-hidden-throttle
**Roadmap ref**: plans/OPEN-DEFECTS.md:108
**Effort**: 30 min
**Risk**: LOW
**Blast radius**: `VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs` (+1 using, ~4 guard lines) · `VPNRouter.Tests/ConnStatsVisibilityThrottleTests.cs` (new, ~1 test, ~20-30 LOC) · ~+30-40 LOC total · runtime: skips one clash_api `/connections` poll per 2 s tick while the window is hidden/minimized
**Rollback**: `git revert <commit>` / branch delete

## Why

The desktop STATS parity poll (`MaybePollConnStats`, fired from the existing
2 s runtime-status timer while connected) always calls the sing-box clash_api
`/connections` endpoint and parses the response — even when the Avalonia
`MainWindow` is hidden to tray (`IsVisible == false`) or minimized
(`WindowState.Minimized`), i.e. when the stats line is off-screen and the work
is pure waste. This is the open perf-hunt F2 follow-up recorded at
`plans/OPEN-DEFECTS.md:108` ("Conn-stats poll has no visibility/minimized
throttle … polls `/connections` every 2s even when the stats line is off-screen
or the window is minimized"). The streaming-parse half (F2) already landed;
this adds the missing visibility throttle. No new timer, event, interface, or
dependency — just an early guard on the window state using the already-existing
private `GetMainWindow()` helper.

## What

Single guard added at the top of `MaybePollConnStats` in
`VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs`, placed **before**
the in-flight `Interlocked.CompareExchange` so a hidden/minimized window never
even claims the in-flight slot, and the API call (`PollConnStatsAsync` →
`GetConnectionsAsync`) stays behind it.

- Null window (no `IClassicDesktopStyleApplicationLifetime`) → skip.
- `!window.IsVisible` (hidden to tray via `Hide()`) → skip.
- `window.WindowState == WindowState.Minimized` → skip.
- Visible + non-minimized → existing behavior unchanged (fall through to the
  in-flight guard + poll exactly as today).

`WindowState`/`Window` live in `Avalonia.Controls` (see
`VPNRouter.App/Services/WindowForegroundHelper.cs:4,40`), so add
`using Avalonia.Controls;` to the ConnStats partial (it currently imports only
`Avalonia.Threading`). `GetMainWindow()` is the existing static helper at
`VPNRouter.App/ViewModels/MainWindowViewModel.cs:7681`.

```diff
 using System;
 using System.Globalization;
 using System.Linq;
 using System.Threading;
 using System.Threading.Tasks;
+using Avalonia.Controls;
 using Avalonia.Threading;
 using CommunityToolkit.Mvvm.ComponentModel;
 using Serilog;
 using VPNRouter.Core.Services;
```

```diff
     private void MaybePollConnStats()
     {
         if (!IsConnected || _statsApi is null) return;
+
+        // Perf-hunt F2 follow-up (OPEN-DEFECTS.md:108): skip the clash_api
+        // /connections poll while the window is hidden to tray (IsVisible=false)
+        // or minimized — the stats line is off-screen, so the work is waste.
+        // Null window (no desktop lifetime) also skips. Visible + non-minimized
+        // keeps the existing behavior below unchanged.
+        var window = GetMainWindow();
+        if (window is null || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
+
         if (Interlocked.CompareExchange(ref _statsInFlight, 1, 0) != 0) return;
         _ = PollConnStatsAsync();
     }
```

## How

1. Add `using Avalonia.Controls;` to `MainWindowViewModel.ConnStats.cs`.
2. Insert the visibility/minimized guard (above) into `MaybePollConnStats`,
   after the `!IsConnected || _statsApi is null` early-out and **before** the
   `Interlocked.CompareExchange(ref _statsInFlight, …)` line. Reuse the existing
   `GetMainWindow()` helper — do not add events, a new timer, an interface, or a
   dependency.
3. Add `VPNRouter.Tests/ConnStatsVisibilityThrottleTests.cs` (~20-30 LOC) — one
   source-shape regression test. Locate the source directly with
   `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
   "../../../../VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs"))`
   and `File.ReadAllText`, then assert guard ordering. Do NOT copy/create a
   `FindRepoFile`/`LoadSource` helper.
4. Build + run the focused test, then full build + full test suite.
5. Fill the Outcome section + mark `plans/OPEN-DEFECTS.md:108` resolved.

### Tests written

- `ConnStatsVisibilityThrottleTests.MaybePollConnStats_GuardsVisibilityBeforeInFlightAndApiCall`
  — source-shape pin on `MainWindowViewModel.ConnStats.cs`. Reads the file text
  and asserts ordering, not runtime:
  - the guard tokens are present: `GetMainWindow()`, `IsVisible`, and
    `WindowState.Minimized`;
  - the guard index is **before** the in-flight
    `Interlocked.CompareExchange(ref _statsInFlight` index (proves the
    visibility/minimized guard sits ahead of the compare/exchange);
  - the in-flight compare/exchange index is **before** the
    `PollConnStatsAsync()` dispatch (and `GetConnectionsAsync` remains inside
    `PollConnStatsAsync`, i.e. behind the guard) — proves the API call stays
    behind the new guard.
  - Uses `Assert.True(idxGuard >= 0 && idxGuard < idxInFlight && idxInFlight <
    idxApi, …)` with a descriptive message so a future reorder/regression fails
    loudly.

### Verification approach

- Focused: `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
  --filter "FullyQualifiedName~ConnStatsVisibilityThrottleTests"`.
- Full: `dotnet build VPNRouter.sln -c Release` (0 errors) + full
  `dotnet test` suite green with the new test included.
- No UI surface change (behavior is a no-op when the window is visible +
  non-minimized), so no MCP screenshot / characterization diff required.

## Verification gate
Check off each as you complete:

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [x] **Gate 2 — Tests green**: full suite passes, new `ConnStatsVisibilityThrottleTests` included.
- [x] **Gate 3 — Docs**: brief Outcome filled; `plans/OPEN-DEFECTS.md:108` marked resolved. No README/CLAUDE.md change (internal perf, not user-facing/architecture).
- [x] **Gate 4 — Self-review**: ponytail/self-review pass over the diff; `security-review` N/A (no trust boundary crossed — same in-process loopback clash_api, no new input/secret/network surface).
- [-] **Gate 5 — MCP verify**: N/A — no visual/layout change; guard is a no-op when the window is visible + non-minimized.
- [-] **Gate 6 — Characterization diff**: N/A — not a god-file split.

## Outcome (filled after merge)

**Status**: PASS
**Commits**: brief `dba92dae`, implementation `1c98679f`
**Pushed**: `origin/qwen/connstats-hidden-throttle`; draft PR #94
**Test deltas**: +1/-0 (new `ConnStatsVisibilityThrottleTests`)
**Files changed**: 4 — this brief (+154 pre-Outcome), `MainWindowViewModel.ConnStats.cs` (+7), new `ConnStatsVisibilityThrottleTests.cs` (+39), `plans/OPEN-DEFECTS.md` (net +1: +2/-1 — resolved entry replacement + new P2). Implementation commit `1c98679f` total +48/-1.

**Gate results:**
- [x] Gate 1: explicit `dotnet build VPNRouter.sln -c Release` → 0 errors, 1 pre-existing warning (`tools/VpnRouterTestMcp/McpServer.cs`); pre-push incremental build also 0/0.
- [x] Gate 2: focused new test 1/1; pre-push scoped suite 185/185; GitHub checks `test`, `go-test-windows`, `grep` all green on `1c98679f`. Local full suite: first run hit 23 `UnauthorizedAccess` failures against real ProgramData; with an isolated temporary ProgramData → 2706 passed / 2 skipped / 3 failed. Those same 3 fail identically on clean `origin/main` (VisualDiff Tools + DpiBypass both at 7.22%, and `VpnEngineSplitTunnelLifecycleTests.Stop_SplitTunnel_FiresRestoreThroughDnsHardening`) — base comparison confirms no feature regression; CI is authoritative green.
- [x] Gate 3: Outcome filled; ConnStats P2 (`OPEN-DEFECTS.md:108`) marked resolved pending release; README/CLAUDE unchanged.
- [x] Gate 4: ponytail clean; independent Qwen review found no blocker. Low/info items triaged: `PollConnStatsAsync` already catches/finally; a behavioral test would require unjustified VM/window/API seams, so the existing project-style source pin is retained; delayed auto-selected refresh is UI label/highlight only and catches up ≤2 s. `security-review` N/A — no trust boundary crossed.
- [-] Gate 5: N/A — no visual/layout change.
- [-] Gate 6: N/A — not a split / public surface; GitHub characterization skipped.

**Surprises encountered**:
- Non-elevated full suite is not hermetic after the SEC-2 ACL: 23 `UnauthorizedAccess` failures against real ProgramData, and the VisualDiff/DpiBypass + split-tunnel lifecycle base failures surface only without isolation. Recorded as evidence for the harness follow-up below. Production ACL must NOT be weakened to make tests pass.

**Follow-ups spawned**:
- New open P2 in `plans/OPEN-DEFECTS.md`: make the non-elevated full test suite hermetic (isolate ProgramData) so it runs clean without touching real system state. The two visual (7.22%) + one lifecycle base failures are the evidence.

**Lessons for methodology doc** (if any):
- None.
