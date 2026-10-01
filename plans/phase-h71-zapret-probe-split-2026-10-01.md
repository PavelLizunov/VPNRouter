# H-71: split `MainWindowViewModel.ProbeAndStartZapretAsync`

## Why

The method (235 lines, Windows only) was one of the functions still over 200 lines (ledger entry
LONG-FUNCTIONS-REMAINING). H-70 pinned its observable behaviour with 27 characterization tests, which is the precondition
named in the ledger for splitting it.

## What

Pure extraction, no logic change. The method keeps the setup, the `try`/`catch`/`finally` and the early-winner log, and
calls five new private methods in the same order:

- `RestoreOrphanedIpsetBeforeProbe(zapretDir)`: the pre-probe cleanup of an orphaned ipset flag.
- `TryStartFromProbeCacheAsync()`: the cached-winner path; returns true after a cache hit (the old `return;`).
- `RunFlowsealSweepAsync(zapretDir)`: progress reporting, the cancellation source and the sweep call; returns the result.
- `StartSweepWinnerAsync(sweep)`: starting the winning strategy, the running check, the cache and settings update.
- `ReportSweepWithoutWinner(sweep)`: the fallback status text and the logged script error lines.

The `finally` block (ipset restore, probing flags, elapsed timer) stays in the original method, so every early `return`
still runs it. The method is 67 lines now; the longest new one is `StartSweepWinnerAsync`.

## Verification

- Line multiset comparison with `tools/refactor-equivalence/line-multiset.py`: nothing removed, only method headers,
  braces, the five calls and `return true;`, `return false;`, `return sweep;` added.
- CI: `MainWindowViewModelZapretProbeTests` (27 methods), the other Zapret tests, the full suite and the Windows
  characterization job.

## Outcome

Merged after green exact-head CI.
