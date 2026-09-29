# H-12: run the full suite on Windows in CI

## Why

Audit (2026-09-29): the `characterization-windows` job runs only a filtered
subset. Many tests are Windows-only and skip on the Ubuntu job, so the lifecycle,
DNS-lockdown and firewall tests (for example `VpnEngineDnsLockdownLifecycleTests`)
were never executed by CI. A regression test added in H-11 would not have run.

## What

Widen the Windows filter to every test, keeping the existing tokens that
`PostShipVerifierContractTests` pins, and exclude the screenshot, visual-diff and
headless GUI classes that still lack a display bootstrap.

## Risk

Tests that were never executed in CI may fail on a clean runner (they were
described as green on clean CI in 2026-06). If the run is red, list the failing
tests here instead of merging; do not weaken assertions to get green.

## Outcome

- First full run: 6 failing `ZapretActionsTests` (stale `sc` executable matcher after
  the product began resolving the system path; two fixtures used directory names
  Windows forbids) and a hang of about 10 minutes.
- Second run with `--blame-hang-timeout`: 1,465 passed, 15 skipped, 0 failed, then
  the host hung in `TunAdapterPnpSettleGateTests.Restart_DoesNotSpawnUntilQueuedRemovalCompletes`.
  Cause: nine test classes replace the same static `TunAdapterDiagnostics` seams and
  ran in parallel. They now share the serial `SafeModeStateCollection`.
- Third run: pending. The hang guard stays in the workflow.
