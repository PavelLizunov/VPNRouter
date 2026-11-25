# Standard category selection during an active connection

- Branch: `dsh/standard-connected-category-autoselect-20261125`
- Base: `ac85310f2eebb75e7e0b4a858b8e7fcaf0dbcaac`
- Risk: Low. This adds a bounded existing probe before committing a connected category choice.
- Rollback: Revert the picker change and its regression tests.

## Why

Standard v2.51.1-r3 can take the first eligible subscription row when the
user chooses an automatic category while connected. The reconnect and
service-apply paths receive that unchecked row. Cold Connect checks the
category candidates and selects by liveness and latency.

## What and how

Use the existing category probe on a connected choice before saving the
winning row or reconnecting. Keep the existing timeout, preferred-row
fallback, exact manual selections, service Apply confirmation, and category
boundaries. Discard a result when connection or selection state changes in
flight. The field report does not identify the precise selection path.

## Verification

1. Build: GitHub-hosted Release solution build on the exact head.
2. Tests: existing suite and picker regressions for a connected choice and a stale result.
3. Docs: record the results here and update the owning defect entry.
4. Review: check selection and transition guards, and confirm no new network path.
5. UI: existing headless picker fixtures. Installed verification follows a candidate ship.
6. Characterization: hosted Windows suite exercises the existing public-surface contract.

## Outcome

Pending CI. The installed r3 test on WINBRAT independently reached core
startup and timed out during TUN warm-up. The verifier left the tunnel
absent. The test VM lacks .NET SDK 10.0.404 for the full visual suite.
