# H-10: kill the launched process when a start fails after spawn

## Why

Audit finding NIGHT-FOLLOWUP-01 (confirmed 2026-09-29): `SingBoxManager` raises
`Started` inside `LaunchProcess`. If a subscriber throws, the catch blocks in
`StartWithJsonCore` and `RestartCore` mark the manager Failed and release the
TUN lease while the spawned sing-box stays alive, so another instance can
start a second sing-box on the same adapter.

## What

Both catch blocks first call `KillLaunchedProcessBestEffort`, which suppresses
the Exited event and kills a still-running handle. The start still fails with
the same exception. One Windows regression test in
`SingBoxManagerRestartTunLockTests` (that class runs in the Windows CI job).

## Not covered

Linux and macOS launch through pkexec or sudo; their wrapper handle is not the
sing-box process, so this best-effort kill does not replace the ownership-based
stop there.

## Verification

Exact-head CI: Windows characterization job runs the new test; full Linux
suite compiles Core. No local SDK.

## Outcome

Merged as #346 on 2026-09-29; exact-head CI green.
