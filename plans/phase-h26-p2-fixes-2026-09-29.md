# H-26: old P2 ledger defects with concrete fixes

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

- Ledger MTU-5 (confirmed on WINBRAT 2026-08-03): editing `TunMtu` in the Network page updated the
  warning and the view model but never called `SaveSettings`, so `1600` reverted after a restart.

Also checked and found already fixed (closed in the ledger separately): `RunFlowsealProbeAsync`
builds its PowerShell start info with `ArgumentList`.

## What

- `MergeWithCache` also carries `LastVerifyFailedAt` and `LastDeepVerifyAt`.
- `CrashReporter.WriteReport` uses `DiagnosticsRedactor.RedactLogText` (which includes `ScrubSecrets`)
  for the exception and each tail line.
- `FirewallManager.TryCleanupOrphanedRulesSafe` dispatches to `MacFirewallManager` /
  `LinuxFirewallManager` `.TryCleanupOrphanedRulesSafe` on those platforms (Windows unchanged). No
  unit test: the Unix methods shell out to `sudo`; the dispatch is a four-line platform branch.
- `MainWindowViewModel.OnTunMtuChanged` saves and marks routing changed when the value is at least
  the minimum (values above the maximum are saved clamped by `SaveSettings`); shorter prefixes typed
  on the way to a valid number are ignored. Tests in `TunMtuPersistenceTests`.
- Tests: `MergeWithCache_CarriesDeepVerifyMemoryToFreshEntry`,
  `WriteReport_TailRedactsBareKeyValueSecrets`.

## Not covered

The separatorless `idtoken`/`appsecret` regex gap named in the ledger entry is not changed.

## Verification

Targeted tests on `windows-worker` at the exact head SHA, negative check without the fix lines, then
exact-head CI.

## Outcome

On `windows-worker`: at cbb16167 `CrashReporterScrubberTests` + `FreeConfigAggregatorPreserveTests` 77 of
77, negative check with both fixes reverted fails both new tests; at 669fc9f7 the firewall, crash
reporter, aggregator and start-command classes 205 of 205; at 93478570 the MTU, MainWindowViewModel
(including the public-surface hash) and Mtu tests 126 of 126. Merged as #361; exact-head CI green.
