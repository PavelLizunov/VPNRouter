# DeepSeek harness reliability

## Why

The WINBRAT evaluation of 484c4942 measured 105 correct state changes in
196 model scenarios. All 25 direct-route requests failed. The live run
returned an already-configured status for a proxy route after a direct request.

## Scope and invariants

- Improve the assistance harness and instructions without replacing the model.
- Give supported per-app intents distinct effects. Verify the active route
  against the requested direction and preserve unrelated configuration.
- Bind action claims to an observed result. Preserve subscription and privacy
  boundaries, cancellation, session ownership, and rollback.
- Measure a sealed holdout with exact state checks and report unknown or blocked
  scenarios separately. No production rollout is part of this task.
- Roll back by reverting the task commits. Older clients must keep their wire
  contract and behavior.

## Verification

1. Release build on an authorized runner.
2. Focused routing, state, and command-validation regressions plus hosted suite.
3. Record direct-route and false-finish findings and observed outcomes.
4. Review the action boundary, route preservation, and claim verification.
5. Measure held-out actions and token usage, with Windows routing acceptance
   where available.
6. Mechanical extraction is out of scope.

## Outcome

Pending implementation and verification.
