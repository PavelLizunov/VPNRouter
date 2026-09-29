# H-49: split SettingsLoader.LoadCore into recovery steps

## Why

`SettingsLoader.LoadCore` (137 lines) loaded `config.yaml` and, in the same body, handled four different recoveries
(missing file, unreadable file, unparsable YAML, settings rejected by the validator), then the clash-API-secret
persistence and the one-line load summary. The happy path (read, parse, validate, return) was buried in the
recovery code.

## What

`LoadCore` now reads: safe mode, `CreateDefaultsAndWriteExample` when the file is missing, `TryReadConfig`, `Parse`
with `RecoverFromUnparsableConfig` in the catch, the validator warnings, `RecoverFromInvalidConfig`,
`PersistClashApiSecretIfMissing`, `LogLoadedSummary`. The statements of each step moved unchanged; the only new
lines are the method headers, the calls, and the `out` handling of `TryReadConfig` (`yaml = string.Empty; return
false;` replaces the old early `return CreateDefaults().EnsureSane();`, which `LoadCore` now performs). The
multiset comparison of trimmed lines shows only `string yaml;` removed and headers, calls and those lines added.

## Verification

Exact-head CI (full suite: `SettingsLoaderRobustnessTests` covers the unreadable, unparsable and invalid cases);
read of the diff.

## Outcome

Pending CI.
