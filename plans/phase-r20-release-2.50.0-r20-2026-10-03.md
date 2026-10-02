# R-20: window fit, candidate v2.50.0-r20

## Why

A real Windows screenshot of r19 on WINBRAT (PrintWindow capture of the deployed app) showed the redesigned home screen (about 700 px of content) inside the 640 px default window: the emblem scrolled under the
header and "Advanced settings" sat below the fold. D-17 (#481) makes the default window 760 px and shortens it on smaller screens. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r20` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the
   recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`; real Windows screenshots of the deployed build for the owner.

## Not covered

Stable cut, live-update gate; hover states with a real cursor; Android; the service/GUI ownership problem; the real update that confirms the driver-lock fix.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Pending.
