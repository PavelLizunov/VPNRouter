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

Pending.
