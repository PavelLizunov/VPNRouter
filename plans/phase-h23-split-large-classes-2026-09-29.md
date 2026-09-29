# H-23: split five large classes by concern (step 2 of the refactor)

## Why

After H-21 the next largest single-class files were `CustomConfigInjector` (1593 lines),
`FreeConfigsPageViewModel` (1478), `VpnEngine` (1234), `SplitTunnelDriverManager` (1017) and
`UpdateChecker` (1012). Each mixes several concerns in one file. Same approach as H-21: mechanical
move, no behavior change.

## What

Whole members (with attributes) and whole depth-1 `#if` blocks move by script into partial files
next to the original; classes that were not `partial` get the keyword.

- `FreeConfigsPageViewModel`: `.Saved`, `.Verification`, `.UserSources`, `.Memory`.
- `CustomConfigInjector` (static): `.Routing`, `.Dns`, `.Geo`, `.Compat`.
- `VpnEngine`: `.Failover`, `.TrueSplit`, `.ProfileSources`, `.StartupHost` (the 239-line nested
  host class).
- `UpdateChecker`: `.Apply` (Windows, macOS, Linux), `.Receipt`, `.Staging`.
- `SplitTunnelDriverManager`: `.Service`, `.Wfp`, `.Device`, `.Pump`, `.NetChange`.

The tool (`splitclass.py`, session scratchpad) checks that the multiset of non-blank lines is
identical before and after (only the `partial` keyword line differs), that every new file is brace
balanced, and that no unit starts with a continuation token.

## Not covered

No logic, name or signature change; no using-directive pruning. Android source-links
`VPNRouter.Core/**/*.cs`, so the new files are picked up there automatically.

## Verification

Full `VPNRouter.Tests` on `windows-worker` at the exact head SHA, Android Release build on the same
worker, then exact-head CI (Ubuntu plus the Windows job).

## Outcome

- First generation failed on the worker: new files lost the `#if PLATFORM_WINDOWS` guard around
  `using System.Management` and `VpnEngine`/`UpdateChecker` lacked `partial`. The splitter now copies
  the whole file header verbatim and always adds `partial`.
- Fixed head: Android Release build on `windows-worker` 0 errors, 84 warnings (same as before the
  split); full `VPNRouter.Tests` 2777 cases, 2753 passed, 17 skipped, the same 7 known failures as on
  `origin/main` (6 `PageScreenshotTests`, 1 fixed by H-19). Exact-head CI: pending.
