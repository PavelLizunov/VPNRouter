# Stable cut: v2.50.0 from the candidate v2.50.0-r29

## Why

The owner authorized the stable release (in Russian, "yes, release the stable version") after the full verification of r29. v2.49.3 is the current stable; v2.50.0-r1 .. r29 are prereleases. Stable users get the new version through the in-app update banner.

## What

1. Stable commit (this PR): `AppVersion.Version` becomes `2.50.0` and `CURRENT_STATE.md` points at the stable. The tree is otherwise identical to the tested candidate `99e6b09a` (plus the docs-only #542).
2. After merge: annotated tag `v2.50.0` on the merged main commit, draft release (non-prerelease, `--latest=false`), six Windows files (unsigned, SignPath is not configured) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, publish with `--latest`, then integrity and APT runs, Homebrew tap notification if a token with contents-write access exists, post-ship on the final stable, and the live update from the last candidate to the stable.
3. Release notes cover everything since v2.49.3, because stable users skip all candidates.

## Not covered

macOS and Linux on real machines, Android on a device and a real tunnel on Android, installing an older version from the Other versions list, urltest switching under a degrading server, signed Windows files.

## Verification

The mandatory previous-stable to candidate live-update gate (`tools/brat-verify.ps1 -Action liveupdate`, v2.49.3 to v2.50.0-r29, then two cold cycles) on WINBRAT before this PR is merged; exact-head CI on this PR; the rest of the gates are the ones of `ship-rolling-candidate` with the stable tag.

## Rollback

A published stable tag is immutable and its assets are never replaced; a correction is a new version. Emergency measure with the owner's approval: mark v2.50.0 as a prerelease and make v2.49.3 Latest again so the update banner stops offering it; users who already installed v2.50.0 can go back through Settings > Updates > Other versions (desktop). Android has no downgrade path.

## Outcome

Published 2026-10-04 as the stable `v2.50.0` (Latest; the previous stable was `v2.49.3`), annotated tag on main `8afa4f26`, 18 uploaded assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and sidecar before upload; macOS, Linux and Android from the tag workflows; GitHub shows 20 on the release page because it adds the two automatic source archives). The tree equals the candidate `v2.50.0-r29` (`99e6b09a`) except `AppVersion`, `CURRENT_STATE.md` and this brief.

Gates, in order:

| gate | result |
|---|---|
| live update gate on WINBRAT, `v2.49.3` deployed from its ZIP, then the real updater to `v2.50.0-r29` | PASS: helper done, exit codes of the copy zero, installed version matches, app relaunched, receipt consumed; then 2 cold cycles PASS and cleanup PASS (a local run of `tools/brat-verify.ps1 -Action liveupdate` through `tools/brat-local-shim.ps1`, like `post-ship-local.ps1`) |
| exact-head CI on the stable PR #543 | all checks green |
| tag workflows (macOS, Linux, Android, test, Windows update) | all green |
| draft integrity (`verify-release-integrity.yml`, dispatched at the tag) | success |
| strict commit gate with explicit requirements | `OK`, 8 green |
| `check-open-p0.ps1` | 3 open P1 lines, waived with the recorded reasons below |
| publication integrity, APT, Windows update test on the release event | all success |
| Homebrew tap | event sent, cask updated to 2.50.0 with the hash of the public DMG |
| `post-ship-local.ps1 -Version 2.50.0` | `POSTSHIP-LOCAL: PASS` (2 cold cycles) |
| live scenarios on the installed stable | cycles: connect 3.3-3.4 s, stop 4.0-4.3 s, IP restored every time; versions list contains v2.49.3; tabs 0 failures; no log findings or crash events |

Waiver of the three open P1 ledger lines (owner decisions of 2026-10-04): the credentials follow-up is confirmed revoked at the provider by the owner (closed in the ledger, no history rewrite); VPNCTL-04 stays open as a deferred feature (Android stays on the 1.13.10 core, desktop is unaffected, the release notes say so); the UDP ephemeral-port event 4266 of 2026-08-09 is closed as not reproduced (the owner reports plenty of free ports; WINBRAT has no such event since 2026-06-18 and 38 UDP endpoints after the test runs; the dev machine log itself was not re-read).

The first two attempts to build the Windows files failed while downloading `sing-box-vpnctl` (GitHub returned an HTML error page twice, the same URL worked from the worker and from here right after); the third attempt was clean. Nothing had been uploaded by the failed attempts.

Not verified: macOS and Linux on real machines, Android on a device and a real tunnel on Android, installing an older version from the Other versions list, the candidate-to-stable update path (the update tooling did not change; the same tree was updated from `v2.49.3`), urltest switching under a degrading server, signed Windows files.
