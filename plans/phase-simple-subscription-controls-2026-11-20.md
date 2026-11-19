# Simple subscription controls and DNSTT startup triage

- Branch: dsh/simple-subscription-controls-20261120
- Base: 1cf2d47fe3b44854e83ab8ad7ec021cee22fdfcd
- Risk: Medium. Subscription refresh can change a selected route.
- Rollback: Revert the scoped commit on the task branch.

## Why

On Standard Windows, the Simple server picker displays latency without offering
a check or refresh. The owner also reported a DNSTT connection timing out and
unreachable servers reappearing after a refresh.

## Scope and method

Expose the existing server check and subscription refresh on the Simple page.
Use its expanded picker and show the existing result text. Respect disabled
registrations and retain the selected route under the existing refresh policy.
Distinguish live feed contents from cached rows and actual protocol reachability
from a quick latency check. Trace the Windows DNSTT startup through transport,
controller confirmation and app status. Change startup only if evidence identifies
a cause.

## Verification

- Build and tests: GitHub CI at the exact head, with focused binding coverage.
- Docs: Record outcome and confirmed defects in the ledger.
- Review: DeepSeek on the exact code diff.
- UI: Headless Simple page and shipped baseline on WINBRAT.
- Characterization: Confirm selection, disabled feeds and refresh behavior are
  preserved. No mechanical extraction is planned.
- Integration: Verify the released build with the supplied feed on WINBRAT and
  capture only aggregate outcomes.

## Outcome

In progress.
