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

Pending CI.
