# H-62: move the Flowseal sweep's output state into its own class

## Why

`ZapretAutoStrategy.RunFlowsealProbeAsync` (355 lines) kept the parsing state of Flowseal's `test zapret.ps1` output in
a dozen local variables captured by an 120-line `OutputDataReceived` lambda, and read them again after the process
exited. With the nine tests of H-61 in place it is now safe to pull that state out.

## What

- New private class `FlowsealSweepState` in `ZapretAutoStrategy.cs` holds what the lambda captured (output text,
  counters, current strategy, per-strategy scores, the last eight error lines, winner, early-winner flag) behind
  `HandleLine`, `SnapshotErrors`, `RecordCurrentStrategy`, `SnapshotScores` and the properties `Winner`, `TestedCount`,
  `TotalCount`, `EarlyWinnerKilled`, `Output`. The early kill of the script is passed in as a callback.
- `RunFlowsealProbeAsync` keeps the environment checks, the probe log, the process start, the stdin answers, the
  cancellation and timeout handling, the orphan clean-up and the result; the lambda is one call to `HandleLine`.
- The statements are the old ones with locals renamed to fields; a multiset comparison of the trimmed lines shows exactly
  that (each removed line has a renamed twin) plus the class header, fields, constructor and properties.

## Verification

The nine `ZapretFlowsealProbeTests` (green on `windows-worker` and in exact-head CI) pin the progress sequence, scores,
fallback winner, early kill, error cap, cancellation and probe log; full suite in CI.

## Outcome

Merged after green exact-head CI.
