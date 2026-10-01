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

Pending.
