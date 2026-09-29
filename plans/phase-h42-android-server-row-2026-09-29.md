# H-42: one Android server row builder instead of two copies

## Why

The duplicate scan (2026-09-29) matched `AndroidApp.BuildServerRow` (server list page) against
`AndroidApp.BuildAggregatedServerRow` (subscriptions page): about 140 lines each, identical except for variable
names, the result and "testing" collections they read and the single-server test method the refresh button calls.
Any visual fix to one row (badge colour, padding, tooltip) had to be repeated by hand in the other.

## What

- `BuildServerRowCore(srv, activeServerName, result, isTesting, onTest)` holds the row; both public builders
  only look up their own result and testing state and pass `TestSingleServerAsync` or
  `TestSingleAggregatedServerAsync`.
- The core takes the result already reduced to `hasResult ? result : null`, so the `hasResult &&` parts of the
  badge and tooltip conditions collapse without changing any outcome.
- The server-list copy built its host text twice (subtitle first, then `server:port - subtitle`); only the
  second value was ever shown. The core builds it once.

## Verification

Line-by-line diff of the two original bodies (only the differences listed above); Android compile check in CI.
There is no device or emulator test in this change (no emulator work is allowed for now).

## Outcome

Merged in #376 after green exact-head CI.
