# Stable cut: v2.50.0 from the candidate v2.50.0-r29

## Why

The owner authorized the stable release ("Да выпускаем стабильную версию") after the full verification of r29. v2.49.3 is the current stable; v2.50.0-r1 .. r29 are prereleases. Stable users get the new version through the in-app update banner.

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

Pending.
