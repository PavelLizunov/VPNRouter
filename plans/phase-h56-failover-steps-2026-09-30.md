# H-56: split AutoFailoverEngine.HandleDeadConfigAsync into steps

## Why

`HandleDeadConfigAsync` (154 lines) checked eight reasons not to switch, changed the active server, restarted with a
rollback on cancellation, reverted on an unconfirmed restart and persisted the choice, with four separate
stale-intent checks between the parts.

## What

The method keeps the stale-intent checks around the selector mutation, the choice of the new name, the tried-set
update, the in-memory switch, the revert on an unconfirmed restart and the final outcome. It calls, in the original
order: `TryRejectBeforeSwitch` (stale intent at entry, custom mode, legitimate manual choice, attempt limit, no
candidate: returns the outcome to surface or `null` and hands back the candidate), `RestartWithRollbackAsync` (the
restart delegate with the revert-on-cancel and the failure handling) and `PersistSelection` (the on-disk update).
Statements moved unchanged; a multiset comparison of the trimmed lines shows one line removed
(`var candidate = PickNextCandidate(...)`, now an assignment to the out parameter) and headers, braces, calls,
`candidate = null;`, `return null;`, `return committed;` and `var candidate = pending!;` added.

## Verification

Exact-head CI (full suite: `AutoFailoverEngineTests`, `AutoFailoverRecoveryAndPersistTests`,
`AutoFailoverUserIntentGuardTests`, `FailoverRestartConcurrencyAuditTests`, `NightFailoverRollbackTests` pin the order
of intent checks, rollback and persistence).

## Outcome

Merged after green exact-head CI (the merge script only merges on green). H-55 (#392) merged the same way.
