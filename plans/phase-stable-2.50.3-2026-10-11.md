# Stable cut: v2.50.3 from the candidate v2.50.3-r1

## Why

A user on stable v2.50.2 cannot connect at all because of a damaged geo rule-set file
(`plans/phase-geo-ruleset-selfheal-2026-10-11.md`). The owner ordered a quick fix release on 2026-10-11 (in Russian:
if the code change is small, make a quick fix release right away; one user has dropped off, maybe more). The affected
users are on the stable channel, so the fix reaches them only as a stable. The agent announced the stable cut in the
session before starting it and treated that order as the stable command.

## What

1. This PR: `AppVersion` becomes `2.50.3`, `CURRENT_STATE.md` names v2.50.3 as the stable, the candidate brief gets its
   Outcome.
2. After merge: annotated tag `v2.50.3` on the merged main commit, draft (non-prerelease, `--latest=false`), six Windows
   files built on `windows-worker`, macOS, Linux and Android from the tag workflows, 18 assets, integrity gate and strict
   commit gate, publication as Latest, Homebrew notification.
3. Release notes in Russian.

## Preconditions (cut-stable), all on the candidate v2.50.3-r1

| # | precondition | evidence |
|---|---|---|
| 1 | AppVersion equals the tag | `2.50.3-r1` at `52eca5d1` |
| 2 | Release build and full suite on the build worker | 0 errors; 3396 passed, 0 failed, 19 skipped (at `946be46f`, same tree) |
| 3 | candidate commit CI | tag workflows, Windows update test, APT, integrity: success |
| 4 | 18 canonical files | 6 Windows, 4 macOS, 6 Linux, 2 Android |
| 5 | post-ship | `POSTSHIP-LOCAL: PASS`, 2 cold cycles |
| 6 | live update from the previous stable | `LIVEUPDATE-LOCAL: PASS (baseline 2.50.2 -> 2.50.3-r1, 2 cold cycles)`, with `geosite-ru.srs` zeroed between the baseline deploy and the update; cleanup PASS |
| 7 | `check-open-p0.ps1` | `OK: no open P0/P1` |
| 8 | owner authorization | the order quoted above |

Not re-run on the stable commit: the full Windows suite on the worker. The stable tree equals the candidate except the
version string and documents; PR CI and the tag workflows run the tests again.

## Rollback

A published stable tag is immutable; a correction is a new version. Emergency measure with the owner's approval: mark
`v2.50.3` as a prerelease and make `v2.50.2` Latest again.

## Outcome

Pending.
