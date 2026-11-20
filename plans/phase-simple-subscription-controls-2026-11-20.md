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

Implemented the expanded picker controls using the existing Advanced commands.
Added an Avalonia regression for real bindings, disabled feeds and refresh
availability during Connect. XML parsing and the scoped diff check passed.

On the verified WINBRAT x64 worker, the published v2.50.6-r3 ZIP and SHA-256
sidecar matched. A direct import of the DNSTT row from the reported subscription
established the app's TUN. The lifecycle verifier saw CoreStarted, then TunReady
1.714 seconds later, with zero classified errors. Stop retired the core and TUN.
A manual refresh in Advanced added the DNSTT row to the cached feed.

The live subscription response contains four direct VLESS routes, four VLESS
routes with a detour, and one DNSTT route. The detour-only capability reproduces
the extra VLESS rows independently of DNSTT. The quick ping deliberately skips
these routes and DNSTT. A skipped probe does not establish reachability or
retirement.

The cached DNSTT row also reached TunReady on WINBRAT in 2.416 seconds after
CoreStarted in subscription mode, with the generated SSH-over-DNSTT outbound.
The affected Windows host confirmed intermittent proxy readiness timeouts.
Three cold checks of the two-destination sequence passed once and returned
504 twice. The readiness change adds a second HTTPS destination and keeps
confirmation mandatory. The affected host remains unverified with a new build.

Two HTTPS tests found three unavailable chained routes for the affected
subscription. The user-specific VLESS overrides removed those rows and the
fresh response retained five VLESS routes and the identical DNSTT profile.

DeepSeek found duplicate requests against an in-flight feed and refresh
responses publishing across Connect or Apply. The shared refresh method now
skips busy feeds and checks connection and Apply generations before publishing.
Simple keeps an empty feed from starting an overlapping fetch. The picker
fixtures cover 380 px, cached and empty feeds, and busy controls. The command
fixtures cover overlapping feeds and completed tunnel transitions.

The previous head passed GitHub CI. The updated fixtures are pending CI.

The previous CI failure was caused by older silent-refresh fixtures using a
simulated Connect to suppress real reconnection. Those fixtures now use the
existing reconnect seam and continue to assert the refreshed row order and hop.
DNSTT review also identified a tight desktop deadline. The two canaries now
have native budgets of eight and six seconds, with one second of controller
allowance each. A two-failure test pins the absence of false readiness.

## Candidate delivery

Prepare Standard `v2.50.6-r4` from the Simple subscription controls and DNSTT readiness changes. The preceding PR head passed all eight checks. Run CI again on the versioned source. The public prerelease requires the release gate decision for #689 and verified Windows, macOS, Linux, and Android artifacts from the accepted revision. The candidate has not been published.
