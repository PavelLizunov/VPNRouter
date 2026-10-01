# R-11: ship the rolling candidate v2.50.0-r11

## Why

Main carries about sixty commits after `v2.50.0-r10`: the Android tile, state and look work, the Windows
installer, the `cleanup` command, the icon set, the redaction fix and the long-function splits. The owner
asked on 2026-10-01 for r11 with "all the new work, including design and the installer", and gave a
standing mandate to decide the rest without asking. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r11` (this PR). It follows #424, so the release has 18 assets.
2. After it is merged: tag `v2.50.0-r11` on the accepted commit and create the draft prerelease with notes.
3. Windows assets are unsigned (owner decision, `plans/phase-signing-off-2026-10-01.md`): `build.ps1 -Version
   2.50.0-r11 -Upload` on `windows-worker` builds the zips, the installer and sidecars and uploads six files to the draft
   without clobber.
4. macOS, Linux and Android come from the tag-triggered workflows; the Windows update test likewise.
5. Prepublication gate: 18 assets, sidecars, integrity workflow, strict CI check; open P0/P1 lines (owner-gated)
   need an explicit `check-open-p0.ps1 -Waive` for this cut.
6. Publication as a prerelease (`--latest=false`); then integrity and APT runs.
7. Post-ship WINBRAT gate: NOT run from here (it needs the owner's offline Windows PC); recorded as not run.

## Not covered

Stable cut. A candidate is not verified until the post-ship gate passes. The Android tunnel on a real phone
(arm64 libbox) is tested later on the tester's phone.

## Verification

Exact-head CI on this PR; the release steps above are recorded in the Outcome as they complete.

## Outcome

Pending.
