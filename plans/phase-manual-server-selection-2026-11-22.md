# Desktop manual server selection

- Branch: dsh/manual-server-selection-20261122
- Base: 83f9bac7857297f6a54099e418b4df536f06792d
- Risk: medium. Server selection feeds both in-session reconfiguration and reconnect settings.
- Scope: Standard desktop settings and the existing server selection controls, with Windows as the first verification target.
- Rollback: revert the implementation commit. The saved auto-select checkbox can still be changed in Subscriptions.

## Why

Selecting a server while auto-select is enabled leaves a live cross-host group in place. Config generation uses `AutoSelectBestServer` to widen the active pool, then adds a three-minute `urltest` interval. A reachable selected host can be replaced on the next measurement. Selecting the currently active subscription row is also short-circuited before reconnect.

## What

Give an explicit server choice precedence over periodic cross-host selection. Save the resulting mode with the chosen route, reusing reconnect or the existing service Apply path. Loading settings, updating subscription rows and observing a runtime member must preserve the user's mode. Keep same-host transport variants, connection-failure recovery and explicit re-enabling of auto-select.

## How

1. Trace manual, subscription and Simple-mode server selectors through settings capture.
2. Apply the change only for explicit selections. Test persistence, config generation for distinct hosts and retention of auto-select during automatic row updates.
3. Check the exact pull request head in hosted CI. Record the result in this brief.

## Verification

1. Build: hosted Release build passed at `1d6cacb7`. Follow-up CI pending.
2. Tests: all eight PR checks passed at `1d6cacb7` (3,855 Linux tests and 4,013 Windows tests). Follow-up CI pending.
3. Docs: diagnosis and review disposition recorded in the defect ledger.
4. Review: independent source review found that navigation disabled auto-select. Removed the navigation override and strengthened the regression.
5. UI: the existing ListBox style and layout are retained. The regression renders the actual desktop pickers. No live install was exercised.
6. Characterization: the Windows characterization suite passed at `1d6cacb7`. No public ViewModel surface change.

## Outcome

The planning commit `cbcf4903` passed both required checks in PR #725.
The three desktop server pickers now pass through Avalonia's accepted row input before turning off auto-select.
The existing selection binding continues to handle settings loads and refreshes. Choosing the same row also disables the cross-host group.
The desktop regression sends a routed arrow key and Enter, checks the actual settings write, exercises service-managed Apply for an active subscription row and regenerates the tunnel configuration.
No installer or product release was requested. Exact follow-up CI is pending.
