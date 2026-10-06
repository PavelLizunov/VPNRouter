# Stable cut: v2.50.2 from the candidate v2.50.2-r1

## Why

The owner ordered the stable release right after the candidate v2.50.2-r1 (in Russian: release it without further checks, "just rename the prerelease to a release") and said the extra checks are not wanted. A flag flip would leave the tag and the binaries named `2.50.2-r1` in the stable channel and in APT, where `2.50.2-r1` sorts above a later `2.50.2`; the project rule is a new immutable stable tag (`cut-stable`), so the stable is `v2.50.2` with the same tree and only `AppVersion` without the suffix.

## What

1. This PR: `AppVersion` becomes `2.50.2`, `CURRENT_STATE.md` names v2.50.2 as the stable.
2. After merge: annotated tag `v2.50.2` on the merged main commit, draft (non-prerelease, `--latest=false`), six Windows files built on `windows-worker`, macOS, Linux and Android from the tag workflows, 18 assets, integrity gate and strict commit gate, publication as Latest, Homebrew notification.
3. Release notes in Russian cover the same changes as the candidate.

## Waived by the owner

The previous-stable to candidate live update on WINBRAT, the post-ship live scenarios and any further emulator runs. The candidate itself passed the full suite, the tag workflows, integrity, APT, the Windows update test in CI and `post-ship-local` (2 cold cycles); the stable tree equals the candidate except for the version string, which PR CI and the tag workflows check again.

## Rollback

A published stable tag is immutable; a correction is a new version. Emergency measure with the owner's approval: mark `v2.50.2` as a prerelease and make `v2.50.1` Latest again.

## Outcome

Pending.
