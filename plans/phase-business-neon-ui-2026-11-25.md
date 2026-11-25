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
