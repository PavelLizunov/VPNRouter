# R-26: the emblem badge fix, candidate v2.50.0-r26

## Why

The owner's screenshot of r25: the round badge under the emblem on the Zapret page is cut off at the bottom (D-22, #510 or the number in the PR list). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r26` (this PR).
2. Contents since r25: D-22 (the `StatusEmblem` control is 144 px tall so the badge hanging below the ring is inside its bounds).
3. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, a zoomed real screenshot of the badge.

## Not covered

Everything listed as open in the r25 brief.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Published 2026-10-03 as prerelease `v2.50.0-r26` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `7bab7148`, 18 assets, same gates as r21-r25 (strict CI gate `OK`, 8 green, waiver gate, integrity run green before and after
publication, `POSTSHIP-LOCAL: PASS` on the first attempt, 2 cold cycles). Contents: D-22 #510. A zoom into a real Windows screenshot of the deployed r26 shows the whole round badge (r25 showed a flat bottom edge). The three Windows baselines were re-rendered on the worker.

Not run: the screenshot gate, the live-update gate; the open items of the r25 brief are unchanged.
