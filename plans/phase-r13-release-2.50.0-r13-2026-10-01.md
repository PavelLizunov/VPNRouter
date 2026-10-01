# R-13: ship the rolling candidate v2.50.0-r13

## Why

The owner tested r12 on a Pixel and reported that, with the "Config / Mode" row open, the main screen did not fit and could not
be scrolled to its end, that expandable rows are not recognisable, and asked for a thorough pass over the interface. The
fixes of that pass (A-5 #439) and the second icon wave (U6 #438) are on main. The owner wants new builds to retest on the phone
and gave a standing mandate to decide the rest without asking. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r13` (this PR).
2. After it is merged: annotated tag `v2.50.0-r13` on the accepted commit, draft prerelease with notes.
3. Windows (6 files, unsigned, owner decision): `relbuild2.ps1` on `windows-worker`, uploaded to the draft without clobber;
   macOS, Linux and Android come from the tag workflows.
4. Prepublication gate (18 assets, sidecars, integrity workflow, strict CI check); `check-open-p0.ps1` with the same recorded
   waiver as r10 to r12 (three owner-gated P1 lines).
5. Publish as a prerelease (`--latest=false`); then integrity and APT runs.
6. Post-ship WINBRAT gate: NOT run from here (needs the owner's offline PC), recorded as not run.

## Not covered

Stable cut. The fixes are verified on an Android 15 emulator, not on the owner's Pixel (Android 16 images crash headless).

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-01 as prerelease `v2.50.0-r13` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main
`a3a6bd78`, 18 assets.

- Preconditions: `verify-last-commit-ci.ps1` OK on `a3a6bd78`; `check-open-p0.ps1` reported the same 3 owner-gated P1
  lines as r10 to r12 and was run with `-Waive` (reason recorded in the command); no SignPath secret exists, so the
  unsigned path applied.
- Windows (6 files) built on `windows-worker` with `relbuild2.ps1` through a lock-aware helper (lock directory
  `C:\android-build\win.lock`; nothing heavy ran there at the time). Each file matched its sidecar, both zips carry the
  split-tunnel driver and `2.50.0-r13` in `VPNRouter.Core.dll`, the installer is a PE file with the version text;
  uploaded to the draft without clobber (GitHub digests equal the local hashes).
- Tag CI green (macOS, Linux, Android, Windows update test, `dotnet test`). The dispatched draft integrity run passed with
  18 assets; strict `verify-last-commit-ci.ps1`: 8 green, 0 red.
- After publication the `release: published` integrity, APT and Windows update test runs succeeded without a manual dispatch.
- Post-ship gate (`tools/post-ship-verify.ps1`): NOT run (needs the owner's offline Windows PC). Run
  `tools/post-ship-verify.ps1 -Version 2.50.0-r13 -Cycles 2` from that PC. The Pixel fixes are to be confirmed on the
  owner's phone.
