# H-13: order the DNS lockdown enable, disable and teardown

## Why

Audit findings WIN-DNS-LOCKDOWN-TOCTOU and WIN-DNS-RESTORE-ORPHAN (2026-09-29).
`WindowsDnsHardening` flipped its flag and started unordered background tasks
for enable, disable and teardown. A slow enable could finish after a disable, so
the firewall rules stayed installed while the flag said they were lifted, and
`Restore` returned before its teardown finished.

## What

- All three operations go through one call-ordered task queue
  (`QueueLockdown`), so they run strictly in the order requested.
- `Restore` waits up to 5 seconds for its teardown, so a normal quit finishes it
  before the process exits; the existing ProcessExit sweep stays as fallback.
- One Windows regression test blocks the first netsh add, requests a disable,
  and asserts no delete runs before every add finishes. Its class name contains
  "Characterization" so the Windows CI job runs it.

## Not covered

The 5 second budget in `Enable/DisableDnsLockdownAsync` still does not stop a
running netsh sequence (WIN-DNS-NETSH-TIMEOUT-DEADLINE).

## Verification

Exact-head CI (Windows job runs the new test).

## Outcome

Pending CI.
