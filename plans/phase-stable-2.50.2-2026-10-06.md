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

Published 2026-10-06 as the stable `v2.50.2` (Latest; the previous stable is `v2.50.1`), annotated tag on main `194eaee1` (PR #554), 18 assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and sidecar before upload; macOS, Linux and Android from the tag workflows). The tree equals the candidate `v2.50.2-r1` (`07097020`) except `AppVersion`; the candidate stays a published prerelease and its tag is untouched.

| gate | result |
|---|---|
| exact-head CI on the stable PR #554 | all green (test, characterization-windows, go-test-windows, grep, Android compile, Windows update test) |
| tag workflows (macOS, Linux, Android, `dotnet test`, Windows update test) | all success |
| draft integrity (`verify-release-integrity.yml` at the tag) | success |
| strict commit gate | `OK`, 8 green |
| `check-open-p0.ps1` | `OK: no open P0/P1` |
| stable APK | 45,345,327 bytes, SHA-256 equals the sidecar, `arm64-v8a` only, libbox tags with_awg and with_xhttp |
| release event: Publish APT Repository, Verify Release Integrity, Auto-Update Integration Test (Windows) | success |
| Homebrew tap `repository_dispatch` | tap run success; cask version 2.50.2 and the DMG hash equal the release sidecar |

Waived by the owner and NOT run: the live update from `v2.50.1` on WINBRAT, `post-ship-local` and the live scenarios on the stable build, any new emulator run. The candidate carried the full suite (3369 passed), `post-ship-local` PASS (2 cold cycles) and the emulator evidence recorded in `plans/phase-android-abi-awg-keyboard-about-2026-10-06.md`. Not verified anywhere: a real Android phone, a real AWG or XHTTP tunnel on Android, signed Windows files.

Why a new tag and not a flag change: the candidate tag `v2.50.2-r1` would have stayed in the stable channel and in APT, where `2.50.2-r1` sorts above a later `2.50.2`; the Homebrew step also refuses tags with an `-rN` suffix.
