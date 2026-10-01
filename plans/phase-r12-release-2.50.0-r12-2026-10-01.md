# R-12: ship the rolling candidate v2.50.0-r12

## Why

The owner tested r11 on a Pixel (Android 16) and found: a very large empty gap above the header, a quick-settings tile
whose status text never changes, a one-off collapsed screen, and old emoji and uneven icons. The fixes are on main
(A-1 #427, A-2 #429, A-3 #433, A-4 #434, U5 #430 #431 #432 #435). The owner asked for new builds to retest on the phone
and gave a standing mandate to decide the rest without asking. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r12` (this PR).
2. After it is merged: annotated tag `v2.50.0-r12` on the accepted commit, draft prerelease with notes.
3. Windows (6 files, unsigned, owner decision): `relbuild2.ps1` on `windows-worker` (`build.ps1 -BundleSplitDriver
   -Installer`), uploaded to the draft without clobber. macOS, Linux and Android come from the tag workflows.
4. Prepublication gate (18 assets, sidecars, integrity workflow, strict CI check); `check-open-p0.ps1` with the same
   recorded waiver as r10 and r11 (three owner-gated P1 lines).
5. Publish as a prerelease (`--latest=false`); then integrity and APT runs.
6. Post-ship WINBRAT gate: NOT run from here (needs the owner's offline PC), recorded as not run.

## Not covered

Stable cut. The Android tunnel on a real phone (arm64 libbox) and the fixes on a Pixel with Android 16 are for the
owner to confirm; Android 16 emulator images crash surfaceflinger headless, so the checks ran on Android 15.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-01 as prerelease `v2.50.0-r12` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main
`8029d81a`, 18 assets.

- Preconditions: `verify-last-commit-ci.ps1` OK on `8029d81a`; `check-open-p0.ps1` reported the same 3 owner-gated P1
  lines as r10 and r11 and was run with `-Waive` (reason recorded in the command); no SignPath secret exists, so the
  unsigned path applied.
- Windows (6 files) built on `windows-worker` with `relbuild2.ps1` (preflight: CPU 1 percent, 13.2 GB free RAM, 15.7 GB
  free disk, nothing heavy running; lock directory `C:\android-build\win.lock`). Each file matched its sidecar, both
  zips carry the split-tunnel driver and `2.50.0-r12` in `VPNRouter.Core.dll`, the installer is a PE file with the
  version text; uploaded to the draft without clobber (GitHub digests equal the local hashes).
- Tag CI green (macOS, Linux, Android, Windows update test, `dotnet test`). The dispatched draft integrity run passed
  with 18 assets, warnings only for the Android assembly store and the AppImage (as before). Strict
  `verify-last-commit-ci.ps1`: 8 green, 0 red.
- After publication the `release: published` integrity, APT and Windows update test runs succeeded without a manual dispatch.
- Post-ship gate (`tools/post-ship-verify.ps1`): NOT run (needs the owner's offline Windows PC). Run
  `tools/post-ship-verify.ps1 -Version 2.50.0-r12 -Cycles 2` from that PC. The Pixel fixes are to be confirmed on the
  owner's phone.
