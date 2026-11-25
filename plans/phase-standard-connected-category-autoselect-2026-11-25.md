# Standard category selection during an active connection

- Branch: `dsh/standard-connected-category-autoselect-20261125`
- Base: `ac85310f2eebb75e7e0b4a858b8e7fcaf0dbcaac`
- Risk: Medium. Category selection now depends on traffic passing through a temporary sing-box process.
- Rollback: Revert the picker change and its regression tests.

## Why

Standard v2.51.1-r3 can take the first eligible subscription row when the
user chooses an automatic category while connected. The reconnect and
service-apply paths receive that unchecked row. Cold Connect checks the
category candidates and selects by liveness and latency.

## What and how

Use the existing isolated sing-box verifier for category choices before
Connect, reconnect, or service Apply. Require a valid HTTPS trace and an
independent HTTPS control response, plus a matching UDP STUN reply for
Games and Voice. Use the configured local DNS for the verifier's bootstrap.
Bound concurrent verifiers and total selection time. If none pass, surface
an unverified result without selecting the first row. Keep exact manual
selections, service Apply confirmation and category boundaries. Discard a
result when connection or selection state changes in flight.

Connected verification binds to an Internet-facing interface outside the
current TUN. If the existing IPv4 gateway selector cannot name one, the
candidate stays unverified. The two HTTPS controls and any UDP probe run
in parallel. Desktop service Apply rechecks the available category. The
20-second category budget can leave later candidates unverified.

The candidate probe establishes reachability through the proposed outbound.
Installed TUN routing, recovery, and traffic attribution remain acceptance
checks on WINBRAT. The field report does not identify the precise selection
path.

## Verification

1. Build: GitHub-hosted Release solution build on the exact head.
2. Tests: existing suite, bootstrap DNS and STUN fixtures, and picker regressions for cold and connected choices, missing proof and stale results.
3. Docs: record the results here and update the owning defect entry.
4. Review: check selection and transition guards, and confirm no new network path.
5. UI: existing headless picker fixtures. Installed verification follows a candidate ship.
6. Characterization: hosted Windows suite exercises the existing public-surface contract.

## Outcome

First independent review caught a stuck transition on disconnect and the
blocked Stop command while checking a connected category. Verification
now has separate cancellation. Parallel HTTPS, UDP retry and explicit
interface binding address the documented probe limitations. A synthetic
sing-box UDP inbound fixture drops one request before replying to the retry.

Independent follow-up review verified these fixes in source and found no
remaining blocker. It did not execute the code. Desktop has no Local DNS
editor, so the verifier reads the loaded profile value. Connected checks
leave Disconnect available and coalesce repeated category clicks while a
verification is in flight. Cold checks use normal interface autodetection.

Git diff checks passed. Hosted Release build, regression tests and the
loopback runtime fixture are pending for the implementation. The installed
r3 test on WINBRAT independently reached core
startup and timed out during TUN warm-up. The verifier left the tunnel
absent. The test VM lacks .NET SDK 10.0.404 for the full visual suite.
