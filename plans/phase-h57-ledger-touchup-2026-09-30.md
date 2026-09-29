# H-57: keep the remaining-long-functions ledger entry accurate

## Why

The P3 entry LONG-FUNCTIONS-REMAINING (added in H-54) listed `SingBoxManager.StopInternal` among the functions over
200 lines, but H-55 (#392) split it the same day.

## What

`plans/OPEN-DEFECTS.md`: `StopInternal` removed from the list, with a note that it and
`AutoFailoverEngine.HandleDeadConfigAsync` (H-56, #393) were split.

## Verification

Docs only; repository documentation checks in CI.

## Outcome

Merged after green exact-head CI.
