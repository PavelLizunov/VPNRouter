# H-46: split Program.Main into named startup steps

## Why

`VPNRouter.App/Program.cs` `Main` was 309 lines: helper-process handling, safe mode, config reset, elevation, logging,
launch-failure counter, service and install health checks with rollback, route-app arguments, single-instance, orphan
cleanup, shortcut and autostart healing, all with several early `return`s inside try blocks. The order of these
steps matters for start-up and was hard to read.

## What

`Main` keeps the same sequence and now reads: helper mode, crash reporter, argument flags, `BackupConfigBeforeSafeMode`,
`ResetConfigAndExit`, (Windows) `RelaunchElevated`, `InitializeLogging`, `RecordLaunchAttempt`, (Windows)
`HealServiceBinPath`, `RunInstallHealthCheck`, `HandleRouteAppArguments`, single-instance, `CleanUpOrphansAndHookProcessExit`,
the shortcut and autostart healing, `StartWithClassicDesktopLifetime`.

The statements of each step moved unchanged. The two steps that could end the start-up from the middle
(`RunInstallHealthCheck`, `HandleRouteAppArguments`) return `false` where the old code did `return;` and `true` at the
end, and `Main` returns when they say so; the Windows-only steps sit in one `#if PLATFORM_WINDOWS` region exactly like
the code they came from. A multiset comparison of the trimmed lines of the file before and after shows only method
headers, braces, the calls and the five `return;` to `return false;` conversions (plus the two new `if (...) return;`
lines in `Main`).

## Verification

Read of the diff against the original order; App builds on Windows and Linux in CI (the `PLATFORM_WINDOWS` split).
Start-up itself is not exercised by any test, and nothing here was run on a desktop.

## Outcome

Merged in #381 after green exact-head CI.
