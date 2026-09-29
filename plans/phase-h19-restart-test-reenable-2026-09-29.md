# H-19: re-enable Restart_DoesNotSpawnUntilQueuedRemovalCompletes

## Why

The test was excluded from the Windows CI job by name in H-12 because it failed on a clean runner.
Diagnosis on `windows-worker` (2026-09-29): it fails deterministically, not by timing. `RestartCore`
returns immediately when the manager does not own the TUN lease, and the test drives
`LaunchProcess` directly without acquiring it, so `Restart` was a no-op and the queued removal
never started until cleanup. Production code is correct; the harness predates the lease guard.

## What

- The test acquires TUN ownership through a reflection helper before launching.
- The name exclusion is removed from the Windows filter in `test.yml`.

## Verification

Targeted run of `TunAdapterPnpSettleGateTests` on `windows-worker` at the exact head SHA, then
exact-head CI (the Windows job now includes this test again).

## Outcome

Targeted run of TunAdapterPnpSettleGateTests on `windows-worker` at 427399a2: 9 of 9 passed, the re-enabled test in 928 ms. Exact-head CI: pending.
