# H-55: split SingBoxManager.StopInternal into Unix and Windows steps

## Why

`SingBoxManager.StopInternal` (253 lines) held the concurrency guard, the macOS/Linux stop (capability mode and the
escalation chain) and the Windows stop (cleanup-only paths, exit probe, kill and confirmation) in one body, with the
try block indented one level too shallow and the same cleanup-only block written out twice.

## What

`StopInternal` keeps the `_stopState` guard, the `_stopInProgress` flag, the log line and the finally block, and
dispatches to `StopUnix` or `StopWindows`. `StopUnix` holds the TUN-ownership guard and chooses
`StopLinuxCapabilityMode` or `StopUnixEscalated`; `StopWindows` holds the null-handle and already-exited paths, the
exit probe, and calls `KillWindowsProcess` for the kill and confirmation. The two identical cleanup-only tails
(state `Stopped`, queued TUN adapter removal, TUN lease release) are one method, `FinishCleanupOnlyStop`. Statements
moved unchanged; the `return`s of the old branches are `return`s of the new methods, and the caller returns after the
Unix call so the outer `finally` runs exactly as before.

A multiset comparison of the trimmed lines shows only one copy of the duplicated cleanup block removed (10 lines) and
method headers, braces and calls added.

## Verification

Exact-head CI: the full suite on Windows and Linux, including the eleven `SingBoxManager*Tests` files (state machine,
concurrent stop, lifecycle stress, restart TUN lock, suppress-exited-event, process-exit leak, TUN orphan recovery,
cleanup path). No new test; the behaviour is pinned by those.

## Outcome

Merged in #392 after green exact-head CI.
