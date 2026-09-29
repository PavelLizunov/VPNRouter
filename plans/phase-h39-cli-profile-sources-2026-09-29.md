# H-39: the CLI uses the same profile sources as the real start

## Why

The cross-file duplicate scan (2026-09-29) matched `VPNRouter.CLI/Helpers/ProfileSourceFactory.cs` against
`VpnEngine.BuildProfileSources`. They are not identical: the CLI copy is older. It skips the platform profiles
(`default-linux.json`, `default-macos.json`, both shipped in `profiles/`), reads `%ProgramData%` through
`ExpandEnvironmentVariables` (a literal path on Linux and macOS) and uses its own priorities. `StartupPipeline`
(the real start from the GUI, the service and `VPNRouter.CLI start`) uses the engine's list, so the CLI commands
that only read profiles (`profiles list`, `profiles show`, `start --dry-run`) could disagree with what a real
start loads on Linux and macOS.

## What

- `StartCommand.DryRunAsync`, `ProfilesListCommand` and `ProfilesShowCommand` call
  `VpnEngine.BuildProfileSources(settings)` (`internal`, visible to the CLI through `InternalsVisibleTo`).
- `ProfileSourceFactory.cs` is deleted; `VPNRouter.CLI/AGENTS.md` no longer lists it.
- Ledger entry CLI-PROFILE-SOURCES-DIVERGED added and closed.

## Verification

Read of the diff; CLI, App and Service build in exact-head CI; the profile source list itself is covered by
the existing engine tests (`VpnEngineOrchestratorTests`). There is no CLI test project, so the Linux and macOS
behaviour of the commands is not exercised automatically.

## Outcome

Merged in #374 after green exact-head CI.
