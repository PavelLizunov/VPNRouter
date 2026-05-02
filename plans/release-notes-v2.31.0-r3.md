# VPNRouter v2.31.0-r3 — ViewModel Pillar (4 leak/race fixes)

Continues v2.31.0 cycle. r3 closes Pillar 3 — four ViewModel-layer
defects from the extended audit: a multi-sub timer that never started,
two CancellationTokenSource leak patterns, and a PropertyChanged
subscription leak across RU↔EN locale toggles.

## Fixes

| ID | File | What |
|---|---|---|
| **VM-1** | `MainWindowViewModel.cs:StartSubRefreshTimer` | Pre-fix the periodic subscription auto-refresh timer bailed if the legacy single `SubscriptionUrl` was empty, but the multi-sub model (since v2.30.2) populates `Subscriptions[]` instead. Users on multi-sub never got auto-refresh. Fix: accept either the legacy URL OR any enabled `Subscriptions[]` entry. |
| **VM-8** | `MainWindowViewModel.LoadApps` | `AppGroups.Clear()` raises CollectionChanged with `action=Reset` and no `OldItems`, so the WireAppChangeTracking handler couldn't unsubscribe stale `PropertyChanged` / `CollectionChanged` delegates. Every RU↔EN locale toggle (which calls LoadApps) leaked one subscription per AppGroupViewModel + per AppItemViewModel. Fix: explicit `UnwireAllAppGroups()` helper called before `Clear()`. |
| **VM-10** | `MainWindowViewModel.ShowRulesToast` | CTS swap pattern cancelled the old token but never disposed it — one `CancellationTokenSource` leaked per toast. Cumulative when toasts flicker rapidly (e.g. user mass-toggles rules on the Network page). Same anti-pattern as CO-2 in r1. Fix: swap+dispose. |
| **VM-11** | `MainWindowViewModel.ResetConfig` | The 5-second auto-disarm `Task.Delay(5000)` was fire-and-forget with no cancellation; clicking arm→disarm→arm rapidly stacked stale Tasks that all eventually ran. Mostly harmless (re-set false=>false) but a leak. Fix: store CTS, cancel old before queuing new, cancel on confirmation. |

## Pattern reuse

VM-10 and VM-11 both adopt the swap+dispose pattern that landed in r1
for `HealthMonitor._restartCts` (CO-2):

```csharp
var oldCts = _xCts;
_xCts = new CancellationTokenSource();
var token = _xCts.Token;
if (oldCts != null) {
    try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
    oldCts.Dispose();
}
```

This is now the canonical CTS-replacement pattern across the codebase.

## Tests

No new tests. The four defects are all in `VPNRouter.App` ViewModel
layer; we don't currently have a headless Avalonia test harness, and
`VPNRouter.Tests` only covers `VPNRouter.Core`. The fixes are small
and the patterns mirror existing tested patterns (CO-2 in
`HealthMonitorTimerRaceTests`).

The VM-1 condition can be observed live by setting up multi-sub
without legacy URL and confirming the timer fires (logs:
`[SubRefresh] Starting timer (interval: ...)`).

## Build / verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors, 18 pre-existing warnings
- 25/25 regression tests pass (no Core changes)

## Cycle progress

| Pillar | Status |
|---|---|
| 1. Core stability (7+1 items) | r1: 7/8 done (AU-9 deferred) |
| 2. A11y systemic (~20 items) | r2: 20/20 done |
| **3. ViewModels (4 items)** | r3: 4/4 done |
| 4. UX closure (8 items) | r4 next |

## Cross-refs

- `plans/vpnrouter-v2.31.0-roadmap.md` — full v2.31 plan
- `plans/release-notes-v2.31.0-r2.md` — Pillar 2 (A11y)
- `plans/release-notes-v2.31.0-r1.md` — Pillar 1 (Core stability)
