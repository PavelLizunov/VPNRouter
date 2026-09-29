# H-52: move the CLI to Spectre.Console 0.57 / Spectre.Console.Cli 0.55

## Why

Dependabot PR #335 (Spectre.Console 0.49.1 to 0.57.2, Spectre.Console.Cli 0.49.1 to 0.55.0) was not mergeable: the CLI
stopped compiling because every command override gained a `CancellationToken` parameter
(`Execute(CommandContext, CancellationToken)`, `Execute(CommandContext, TSettings, CancellationToken)`, and the async
variants). The earlier check of that PR on the worker only built the test project, which does not build the CLI; the
failure was found by CI (see the memory note on dependency PR checks).

## What

- `VPNRouter.CLI.csproj`: the two package versions of #335.
- The 13 command overrides in `Commands/` (`Doctor`, `Profiles` x3, `Service` x5, `Start`, `Status`, `Stop`,
  `TestUpdate`) take the new `CancellationToken cancellationToken` parameter. The parameter is not used yet: none of
  the commands is cancellable through the framework (they use their own Ctrl+C / stop-event handling).

## Verification

Exact-head CI: the CLI, App and Service builds on Windows and Linux (the run that failed on #335) and the full suite.
Nothing was run on a desktop; the command signatures are the only change.

## Outcome

Pending CI. When merged, Dependabot PR #335 is obsolete and can be closed.
