# Headless upgrade recovery (2026-10-26)

## Intent and scope

Continue Claude session faa48ee2-cca3-48b1-b223-9580da496820 and PR #630.
The owner reports that after upgrading on another laptop the VPN still routes
traffic while the plugin shows disconnected and cannot connect. Expected result:
a new default-data-directory Headless owner can recover a legacy stdio helper
or orphaned packaged runtime, without stopping an independently owned session.

The reporting laptop was not inspected: the previous session's SSH attempt
returned connection refused. Orphaned runtime is a source-supported hypothesis,
not a confirmed diagnosis on that laptop.

## Implementation

- Keep recovery limited to Linux default data, never --attach or isolated data.
- Take the service/data leases before recovery or Core construction.
- Match same-user legacy helpers and packaged engines using the default config;
  protect live owners, retain process start identity, signal using pidfds.
- Coordinate the plugin's update wait and startup budgets with bounded cleanup.
- Build a fresh Arch payload from the accepted exact revision and bundle it in
  omarchy-vpnrouter. Preserve the active development branches and user changes.

## Risk and rollback

Recovery deliberately stops superseded default stdio sessions. Package upgrade
starts disconnected and never reconnects automatically. Revert the task commits
and restore the preceding bundle for rollback. Unrelated runtimes, explicit
data directories and service-owned sessions are outside the cleanup scope.

## Verification

1. Build: use the existing exact-SHA Arch CI packaging workflow for the payload.
2. Tests: no new test runs/writing requested in the resumed task; no local tests.
   Existing CI for 9cd045cf completed successfully before continuation.
3. Documentation: this record and the task-owned diff will describe actual work.
4. Review: direct owning-source/diff inspection only; no extra model review.
5. UI/live: no render, VPN connection or reporting-laptop access requested here.
   Target-machine recovery and interactive package upgrade remain unverified.
6. Integration: preserve data/socket ownership and existing transport behavior.

## Outcome

Recovery now executes from the handler factory after the service's data/socket
leases (and the existing stdio lease). Process matching retains start identity
and default config scope; pidfds pin signal targets including escalation.
Live recorded owners and Headless children are preserved. No automatic reconnect
was added. Task-owned diff inspected; local tests and live behavior not checked.

The coordinated plugin aborts package installation on a helper-stop timeout and
extends initial snapshot/socket waits to cover the 26-second cleanup budget.
Delivery completed: PR #630 merged as f6b39dbfc4d4de6e064b470037ea6b5a312c5a4c.
Automatic PR CI passed at 56f87786d70d369ebb158b35bc9d98e664249e3a.
Main Arch CI 40348520604 succeeded and produced 0.1.0.r59.gf6b39db-1.
That exact package is bundled in omarchy-vpnrouter main
4e66c9eb3558d552bed744f2b85e8818018118b9 and published in plugin release v0.1.2:
https://github.com/PavelLizunov/omarchy-vpnrouter/releases/tag/v0.1.2

Source delivery and publication are complete. No package was installed or live
VPN session changed on this workstation or the reporting laptop during the
continuation. The report's actual on-device cause and recovery remain unverified.
The open ledger entry is retained for that material acceptance limitation.
