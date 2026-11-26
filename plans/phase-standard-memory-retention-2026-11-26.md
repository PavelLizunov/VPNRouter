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
Linux pkexec launches the exact core path directly, and the core reads the
memory setting from its runtime configuration. Capability launches retain the
direct-launch override semantics. The target is a Go GC tuning value.

Simple Mode marks Advanced pages inactive without changing tab selection.
Tray trimming releases replaceable country tables. The embedded gzip tables
inflate into their exact-sized arrays. Free Configs drains verification tasks
before releasing semaphores and clears and disposes the three manual command
lifetimes on their normal paths.

Static checks covered whitespace, XAML parsing, embedded gzip member sizes,
the macOS environment literal, and the first Linux launch argument shape. All ten PR
checks passed for `0d6b57b2`, including Windows characterization and macOS
privileged-system acceptance. The Linux test artifact reports 4,647 passes
with no failures, including the rendered Pictogram, process polling, memory
target, and deferred-page regressions.

The second block expires server verdicts after 12 hours and retains the
2,048 most recent entries. It streams large legacy histories using a 64 KiB
record buffer and keeps existing fresh records. The shared process-pattern
cache holds at most 256 entries, and ruleset fetching uses a fixed 64-lock
array. TUN diagnostics reuse their static regex instances.

Clash connection statistics parse in a pooled 64 KiB buffer under a 4 MiB
body limit. Opt-in log health discards individual messages over 256 KiB and
caps its rolling queue at 10,000 events. Oversized and invalid snapshots keep
the existing failure representation. All ten PR checks passed on `3679fd82`.
The Linux test artifact reports 4,712 passes with no failures. This includes
the nested Clash fixture corrected after the first combined CI run.

### Independent review follow-up

Neon identified the invalid nested Clash fixture, a Linux authorization
regression introduced by the `env` wrapper, and incomplete compaction of
duplicate health records. The fixture is covered by the green `3679fd82`
run. The revised Linux launch keeps the previous polkit target and preserves
existing explicit `experimental.debug.memory_limit` values. At pinned core
revision `fe24739a`, the 384 MiB config value yields a 256 MiB Go GC target
through `debug.SetMemoryLimit(value / 1.5)`. The runtime config is replaced
privately and atomically. The pinned-core fixture covers startup with this
configuration, and argument tests exercise executable paths with spaces
and equals signs. Health-store fixtures check last-value semantics and
compaction for duplicate keys and repeated arrays. ETW uses the documented
TraceEvent session disposal path to stop a real-time consumer.

Exact-head CI and the scoped follow-up review are pending. Installed polkit
launch and long-run memory measurements remain platform acceptance items.

The independent watchdog design recommends observing per-process trends first.
Cache cleanup can reuse existing trim paths. Any future owned-core restart
would need a dedicated lifecycle operation with kill-switch and DNS verification.
The current patch addresses source defects directly.
