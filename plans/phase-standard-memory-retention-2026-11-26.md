# Standard desktop memory retention follow-up

- Branch: `dsh/standard-memory-retention-20261126`
- Base: `6538682b20ce6229c9b8efd7c6b4e021ed3c95a3` (`v2.51.1-r4`)
- Risk: High for process ownership and privilege boundaries. Medium for cache eviction and bounded telemetry.
- Rollback: Revert the implementation commits and rebuild through the normal release process.

## Why

Repeat audits of r4 confirmed that the prior Pictogram binding fix shipped. Remaining paths can retain trace workers, polling lifetimes, stale health records, caches, or response buffers. Privileged child launches miss the Go memory target on macOS and on the Linux pkexec path. The source evidence does not identify the cause of the reported 1+ GB on r4.

## What

- Fix ETW startup and teardown ownership and Unix monitor lifetime. Preserve process routing, events, and the no-subscriber fast path.
- Set a default Go memory target on privileged launches while preserving the trusted helper policy and exact runtime ownership.
- Bound server verdict storage and existing scanner and ruleset caches. Bound Clash connection and log telemetry while keeping the valid response paths.
- Release the hidden Advanced page through the existing tray trim path, reduce initial country-table allocation, and complete Free Configs cancellation resource cleanup.
- Evaluate a memory watchdog independently. Automatic recovery policy and release actions are outside implementation scope.

## How

Keep Windows ETW provider setup and stop ordering explicit. Add deterministic worker lifecycle regression cases and pin the environment passed to each privilege path. Preserve server-cache persistence compatibility and exact path serialization. Use synthetic large and broken inputs to test the Clash limits and verify tray page rehydration.

## Verification

1. Build: exact-head CI across the applicable desktop targets and macOS helper contract.
2. Tests: exact-head CI. Focus coverage on monitor cancellation, launch environment, bounded data, and deferred page rehydration.
3. Documentation: record observed evidence and the disposition of verified findings.
4. Review: independent correctness, lifecycle, privilege and test review before handoff.
5. Runtime: headless desktop regression in CI. Installed Windows ETW/long-run memory and privileged environment measurement require the applicable platform workers.
6. Integration: preserve public ViewModel shape, ETW routing and firewall/DNS ownership. Confirm normal telemetry results are preserved.

## Outcome

The first implementation block gives ETW setup exclusive ownership of the
kernel session until readiness or disposal. Stop cancels in-flight setup and
retains the registered worker until exit. Replacement monitors in the same
process wait for the old kernel session to close. The Unix monitor captures
its own cancellation source and interrupts its polling delay at Stop.

The macOS helper sets the fixed Go memory target in the core environment.
The Linux pkexec path sets the same target after authorization, through the
system env command with separate arguments. Capability launches retain the
direct-launch override semantics. The target is a Go GC tuning value.

Simple Mode marks Advanced pages inactive without changing tab selection.
Tray trimming releases replaceable country tables. The embedded gzip tables
inflate into their exact-sized arrays. Free Configs drains verification tasks
before releasing semaphores and clears and disposes the three manual command
lifetimes on their normal paths.

Static checks covered whitespace, XAML parsing, embedded gzip member sizes,
the macOS environment literal, and the Linux env argument shape. Build, test,
and platform acceptance evidence remain pending for this implementation block.

The independent watchdog design recommends observing per-process trends first.
Cache cleanup can reuse existing trim paths. Any future owned-core restart
would need a dedicated lifecycle operation with kill-switch and DNS verification.
The current patch addresses source defects directly.
