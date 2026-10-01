# R-14: ship the rolling candidate v2.50.0-r14

## Why

The design work the owner asked for is on main: Android leftovers (A-6, A-7), the PC app icons (D-1) and the PC app buttons,
toggles and empty states (D-2), plus the worker-local post-ship tooling (P-1). The owner wants new builds to retest on the
phone and the PC, and gave a standing mandate to decide the rest without asking. This brief follows
`.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r14` (this PR).
2. After it is merged: annotated tag `v2.50.0-r14`, draft prerelease with notes; Windows (6 files, unsigned) built on
   `windows-worker` with `relbuild2.ps1` and uploaded without clobber; macOS, Linux and Android from the tag workflows.
3. Prepublication gate (18 assets, sidecars, integrity workflow, strict CI check); `check-open-p0.ps1` with the same recorded
   waiver as r10 to r13; publish as a prerelease (`--latest=false`).
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1` (this release changes the desktop UI, so the deploy and the UI
   automation of the connect cycles are a real check).

## Not covered

Stable cut, the screenshot gate (6 known failing tests), the previous-stable to candidate live-update gate; the Pixel
(Android 16) check is the owner's.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-01 as prerelease `v2.50.0-r14` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main
`572611c9`, 18 assets.

- Preconditions: `verify-last-commit-ci.ps1` OK on `572611c9`; `check-open-p0.ps1` reported the same 3 owner-gated P1 lines as
  r10 to r13 and was run with `-Waive`; no SignPath secret exists, so the unsigned path applied.
- Windows (6 files) built on `windows-worker` with `relbuild2.ps1` through the lock-aware helper. Both zips carry the
  split-tunnel driver and `2.50.0-r14` in `VPNRouter.Core.dll`, the installer is a PE file with the version text.
- Incident, draft only: copying the zips from the worker to the scratch directory hit a disk quota and left truncated
  files (204800 and 102400 bytes); the chained command went on to upload them before the hash check was read. They were
  caught from the digests (equal to the truncated local files, sidecars from the worker did not match). Exactly those two
  draft assets, uploaded by the same session, were deleted and the verified files uploaded; the six Windows digests on
  GitHub now equal the hashes of the worker-built files. The integrity gate ran after the fix, never on the bad assets;
  nothing was public. Lesson: stop a chain on the first hash mismatch and check free space before copying (the scratch
  directory has a small quota; old release copies were removed).
- Tag CI green (macOS, Linux, Android, Windows update test, `dotnet test`). The dispatched draft integrity run passed with
  18 assets; strict `verify-last-commit-ci.ps1`: 8 green, 0 red. After publication the integrity, APT and Windows update
  runs succeeded without a manual dispatch.
- Post-ship on WINBRAT with `tools/post-ship-local.ps1` (the release changes the desktop UI): published zip downloaded and
  hash-checked, clean deploy, two cold UI connect/disconnect cycles (core started twice, TUN ready twice, no health
  failure, restart or failover), `Status: PASS`. NOT run: the screenshot gate (6 known failing tests) and the previous-stable
  to candidate live-update gate (stable cut only). The Pixel (Android 16) check is the owner's.
