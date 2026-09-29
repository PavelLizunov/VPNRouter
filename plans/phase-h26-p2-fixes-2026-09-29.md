# H-26: two old P2 ledger defects with concrete fixes

## Why

Ledger review 2026-09-29 verified two long-open P2 entries in the current source:

- `MergeWithCache` copied status, latency and bandwidth from the cache to fresh pool entries but not
  `LastDeepVerifyAt` (nor `LastVerifyFailedAt`), so the 6 hour "skip deep verify" window in
  `FreeConfigsPageViewModel` was lost on every pool refresh.
- `CrashReporter.WriteReport` scrubbed the exception text and log tail with `ScrubSecrets` only,
  which misses bare `key=value` secrets that `DiagnosticsRedactor.RedactLogText` masks.

- `FirewallManager.TryCleanupOrphanedRulesSafe` returned immediately on Linux and macOS, so the CLI
  start path and its ProcessExit hook (which call it) never cleaned up a leftover kill-switch table
  after `kill -9`; only the GUI did, through the platform managers directly.

Also checked and found already fixed (closed in the ledger separately): `RunFlowsealProbeAsync`
builds its PowerShell start info with `ArgumentList`.

## What

- `MergeWithCache` also carries `LastVerifyFailedAt` and `LastDeepVerifyAt`.
- `CrashReporter.WriteReport` uses `DiagnosticsRedactor.RedactLogText` (which includes `ScrubSecrets`)
  for the exception and each tail line.
- `FirewallManager.TryCleanupOrphanedRulesSafe` dispatches to `MacFirewallManager` /
  `LinuxFirewallManager` `.TryCleanupOrphanedRulesSafe` on those platforms (Windows unchanged). No
  unit test: the Unix methods shell out to `sudo`; the dispatch is a four-line platform branch.
- Tests: `MergeWithCache_CarriesDeepVerifyMemoryToFreshEntry`,
  `WriteReport_TailRedactsBareKeyValueSecrets`.

## Not covered

The separatorless `idtoken`/`appsecret` regex gap named in the ledger entry is not changed.

## Verification

Targeted tests on `windows-worker` at the exact head SHA, negative check without the fix lines, then
exact-head CI.

## Outcome

On `windows-worker` at cbb16167: `CrashReporterScrubberTests` and `FreeConfigAggregatorPreserveTests`
77 of 77 passed. Negative check with both fixes reverted: both new tests fail. Exact-head CI: pending.
