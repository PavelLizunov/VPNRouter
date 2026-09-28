# H-3b: remove comments from scripts and workflows

## Why

After H-3a, scripts, hooks, packaging scripts and workflows still carried about
2,160 comment lines of history and narration. The owner ordered the same hard
removal on 2026-09-29.

## What

A script removes full-line comments from 48 files (`.ps1`, `.sh`, `.py`,
`.yml`, `.cmd`, git hooks, shebang packaging scripts), including PowerShell
`<# ... #>` help blocks. Kept: shebangs, `shellcheck`/`#Requires`/region
directives, trailing comments, and everything inside heredocs, PowerShell
here-strings and non-`run` YAML block scalars. `tools/zapret/` untouched.

## Verification

YAML parses to the same structure (ignoring comment lines inside `run`
scripts); Python token streams identical; `bash -n` passes for every shell
script; every removed line is a comment line or inside a help block; exact-head
PR CI green.

## Outcome

- 47 files changed, 2,164 lines removed; no non-comment line removed.
- One test assertion pinned a usage comment in `verify-release-integrity.yml`
  (`--ref "$TAG"` dispatch example); removed, the procedure lives in the release
  skills. Nine other literal matches were coincidental.
- First CI run: `VpnctlPackagingCharacterizationTests` and one
  `ReleaseToolingContractTests` case sliced `build.ps1` / `install.ps1` between
  comment section headers. Re-anchored on the first code line after each former
  header (all unique), so the executed slices cover the same code.
