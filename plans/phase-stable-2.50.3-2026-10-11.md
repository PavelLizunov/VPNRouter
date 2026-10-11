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

Published 2026-10-11 as the stable `v2.50.3` (Latest; the previous stable is `v2.50.2`), annotated tag on main `8b78853b`
(PR #574), 18 assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and
sidecar before upload; macOS, Linux and Android from the tag workflows). The tree equals the candidate `v2.50.3-r1`
(`52eca5d1`) except `AppVersion` and documents; the candidate stays a published prerelease and its tag is untouched.

| gate | result |
|---|---|
| exact-head CI on the stable PR #574 | all green (test, characterization-windows, go-test-windows, grep, Android compile, Windows update test) |
| tag workflows (macOS, Linux, Android, `dotnet test`, Windows update test) | all success |
| draft integrity (`verify-release-integrity.yml` at the tag) | success |
| strict commit gate | `OK`, 8 green |
| `check-open-p0.ps1` | `OK: no open P0/P1` |
| SignPath settings | none (unsigned Windows files) |
| release event: Publish APT Repository, Verify Release Integrity, Auto-Update Integration Test (Windows) | success |
| Homebrew tap `repository_dispatch` | tap run success; cask version 2.50.3 and the DMG hash equal the release sidecar |
| `post-ship-local` on WINBRAT with the stable zip | `POSTSHIP-LOCAL: PASS`, 2 cold cycles |
| damaged-file scenario on the stable build (geosite zeroed, one Connect through the UI) | repaired and connected in 3.2 s, clean disconnect |

Worker preflight before the stable build: WINBRAT, CPU 17 % of 8, 13.9 of 16 GB free, 111 GB disk, no build process,
SDK 10.0.301. Another project's session was using the same worker; the desktop checks ran in windows agreed with it and
its jobs were not touched.

Not run on the stable commit: the full Windows suite on the worker (candidate tree: 3396 passed, 0 failed, 19 skipped)
and the candidate-to-stable live update. Not verified anywhere: macOS, Linux and Android on real devices for this fix,
a failed download in the field, signed Windows files.

Follow-ups stay in `plans/OPEN-DEFECTS.md` (all P3): `RULESET-CACHE-NO-CONTENT-CHECK`, `GEO-REFRESH-UNREACHABLE`,
`DIAG-RULESET-NOT-SURFACED`, `RULESET-STRUCTURE-NOT-PARSED`, `GEO-DOWNLOAD-TWO-PROCESS-RACE`.
