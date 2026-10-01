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

Pending.
