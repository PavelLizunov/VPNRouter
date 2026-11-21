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

1. Build: pending hosted Release build.
2. Tests: pending selection regressions and PR suites.
3. Docs: source diagnosis recorded. Outcome pending.
4. Review: pending implementation and scoped review.
5. UI: pending verification of affected controls. No layout change planned.
6. Characterization: no mechanical extraction or public API change planned.

## Outcome

The report matches the released source. Implementation and verification are pending.
