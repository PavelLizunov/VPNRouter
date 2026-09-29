# H-40: split HealthCheck.RunAll into named checks

## Why

`HealthCheck.RunAll` (used by the GUI health page, `VPNRouter.CLI doctor`, the Android tools page and the diagnostics
export) was one 269-line method that ran nine unrelated checks in a row with their variables in one scope.

## What

`RunAll` now calls, in the original order, `CheckConfigYaml` (returns the parsed settings for the later steps),
`CheckUserCatalogue`, `CheckSingBoxBinary`, the install receipt / proxy timeout / path MTU lines that stay inline,
`CheckLinuxPrivileges`, `CheckStateFile`, `CheckLockFile`, `CheckProcessInventory`, `CheckDirectories` and the
advice lines. The bodies of the new methods are the old blocks moved unchanged.

## Verification

Worker-only corpus test (not committed): 60 seeded data directories (config.yaml missing, garbage, each config
mode and schema version, user catalogue variants, sing-box binary present or missing, state.json and running.lock
variants with dead and live PIDs, missing directories, current.json plus log) run through
`FormatReport(RunAll())`; the hash of every report at the pre-change SHA and at this SHA must be identical (data
directory path, own PID and the 8.8.8.8 path MTU lines are normalised). Exact-head CI runs the full suite.

## Outcome

Pending equivalence run and CI.
