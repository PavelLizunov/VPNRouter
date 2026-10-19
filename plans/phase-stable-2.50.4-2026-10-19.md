# Stable cut: v2.50.4 from the candidate v2.50.4-r2

## Why

Stable v2.50.3 (like every build since v2.50.0) has no tray icon, so a closed window is unreachable until the next launch
(`plans/phase-tray-icon-guard-2026-10-18.md`). The fix reaches stable users only as a stable. The owner tested v2.50.4-r2
and approved the release on 2026-10-19 (in Russian: tested VPNRouter v2.50.4-r2, you can release). The approval was
written in the Suflyor session and relayed to this session by that session.

## What

1. This PR: `AppVersion` becomes `2.50.4`, `CURRENT_STATE.md` names v2.50.4 as the stable.
2. After merge: annotated tag `v2.50.4` on the merged main commit, draft (non-prerelease, `--latest=false`), six Windows
   files built on `windows-worker`, macOS, Linux and Android from the tag workflows, 18 assets, integrity gate and strict
   commit gate, publication as Latest, Homebrew notification, post-ship on WINBRAT.
3. Release notes in Russian covering everything since v2.50.3 (v2.50.4-r1 and v2.50.4-r2).

## Preconditions (cut-stable), all on the candidate v2.50.4-r2

| # | precondition | evidence |
|---|---|---|
| 1 | AppVersion equals the tag | `2.50.4-r2` at `35f86e92` |
| 2 | Release build and tests | worker release build 0 errors at `35f86e92`; PR #594 CI and tag `test.yml` green |
| 3 | candidate commit CI | tag workflows, Windows update test, APT, integrity: success; strict gate 8 green |
| 4 | 18 canonical files | 6 Windows, 4 macOS, 6 Linux, 2 Android |
| 5 | post-ship | `POSTSHIP-LOCAL: PASS`, 2 cold cycles |
| 6 | live update from the previous stable | LIVEUPDATE_RESULT |
| 7 | `check-open-p0.ps1` | `OK: no open P0/P1` |
| 8 | owner authorization | the approval quoted above |

Not re-run on the stable commit: the full Windows suite on the worker. The stable tree equals the candidate except the
version string and documents; PR CI and the tag workflows run the tests again.

## Rollback

A published stable tag is immutable; a correction is a new version. Emergency measure with the owner's approval: mark
`v2.50.4` as a prerelease and make `v2.50.3` Latest again.
