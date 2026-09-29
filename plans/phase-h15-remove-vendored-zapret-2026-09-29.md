# H-15: remove the vendored Zapret copy

## Why

`tools/zapret/` (18 files, winws.exe, WinDivert, filter lists) was last changed in v2.8.17.
No build, code, test or workflow reads it: `ZapretUpdater` downloads Flowseal releases on
demand and `build.ps1` states Zapret is not bundled. Owner decision 2026-09-29: delete.

## What

- Delete `tools/zapret/`; the files stay readable through git history.
- Remove the "do not delete" wording from `docs/agent-contract.md` and `tools/AGENTS.md`.

## Verification

`git grep` finds no other consumer of the path; exact-head CI.

## Outcome

Merged as #350 on 2026-09-29; exact-head CI green.
