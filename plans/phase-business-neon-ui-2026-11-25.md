# VPNRouter Business desktop redesign

## Intent

Apply the UX review of the released Business client. Employees can connect with
one click and understand the next step when a connection fails. Server and VPN
mode remain available directly on Home.

## Scope and invariants

Refine the Avalonia Business window, its localized copy and view models for
connection recovery, diagnostics and settings. Keep the automatic HTTPS-based
server choice and VPN routing policy. A recovery attempt sends no logs without
a separate confirmation. Sending, receipt and analysis are different states.
Preserve manual preferences, update integrity checks and the Standard edition.
This branch produces a preview for the owner. Roll back the UI with the task
commits if needed.

## Approved design reference

The owner approved the interactive Neon design on 2026-11-25. Use its Home,
server and VPN mode layout, and its recovery, consent and report feedback
states as the implementation reference. The original preview SHA-256 is
`3dc81a57c8bb153b0f8e548691313ea51944c937eec701c5996b59a79a7e8433`.
Verify the implementation with synthetic renders of those states.

## Gates

1. Build the exact branch revision in GitHub CI.
2. Exercise the new interaction and regression scenarios with synthetic data in
   the Business suite, plus the shared suite.
3. Record the implemented scope and verification in the task outcome.
4. Review source changes for interaction state, accessibility and diagnosis
   consent, including cancellation, retry and receipt.
5. Render the actual Avalonia window at narrow widths for the owner. A design
   sketch may precede the render and must be identified as such.
6. Confirm existing connectivity, routing, diagnostics and update contracts;
   installation on the user's machine belongs to release verification.

## Outcome

- Home uses a connection status pill and one primary connection action. The
  settings rows open inline, display the automatic or saved server when offline,
  and use accepted tunnel metadata for connected server and mode. The picker
  pauses probes during verified recovery. Server metadata retains its measured
  last-day traffic activity label.
- Repair has local search and retry, explicit consent, separate collection and
  upload stages, and distinct receipt and advice states. A repair-owned
  subscription refresh can replace rows without cancelling the search. Upload
  failures retry the same archive. Closing during the acknowledgement handoff
  preserves delivery when it is confirmed. Advice stays visible after selecting
  a different server.
- The overflow menu routes support uploads to the consent card. Local report
  collection remains separate. The menu shows updated and embedded list status.
  Business update and rollback copy describes the operation and keeps explicit
  confirmation. The standalone report tool discloses device and user names.
- Static verification parsed all 22 tracked Avalonia XML files and
  cross-referenced the Business window's 150 bindings, 33 resource keys in
  both themes, event handlers, pictograms and localized strings.
  `git diff --check` passed. The approved reference checksum matched.
- The existing Ubuntu and Windows Business policy matrix selects fixtures for
  13 artifact paths covering actionable policy, narrow Home and inline
  settings, and repair states. The standard and UI MCP jobs also select
  the 340 px and 420 px Home settings fixtures. Compilation, execution
  and review of the resulting images are pending GitHub CI. This implementation
  handoff includes no live installation.
- The previous review findings were checked against the integrated source.
  New findings and their dispositions are in OPEN-DEFECTS.md. No new independent
  review ran during this implementation handoff. Rollback is a revert of the
  implementation commit.
