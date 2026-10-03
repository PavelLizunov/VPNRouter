# R-23: the Subscribe page layout fix, candidate v2.50.0-r23

## Why

A real Windows screenshot of the published r22 showed a regression of F-7: the Subscribe tab's server list filled the whole page and the test buttons, subscriptions and the add form sat below the fold (a scroll viewer measures with unlimited height). F-7b (#499)
splits the page into a list area and a scrolling controls area. r22 stays published as a prerelease; r23 replaces it as the candidate to test. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r23` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1` and real screenshots of the Subscribe tab and the home screen.

## Not covered

As r22: Android connected/error ring colours with a real tunnel and the Pixel; the setup check auto-open; stable cut, live-update gate, hover with a real cursor.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Published 2026-10-03 as prerelease `v2.50.0-r23` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `33a1b382`, 18 assets, same gates as r21 and r22 (strict CI gate `OK`, 8 green, waiver gate, integrity run green,
`POSTSHIP-LOCAL: PASS` on the first attempt, 2 cold cycles). Contents: F-7b #499 on top of r22 (Subscribe page: list row 3*, at least 150 px, controls in their own scrolling 2* area; Servers page back to its original layout with a 170 px list minimum).
UI MCP renders showed everything fitting at 520x760 and the list staying visible with a scrolling lower area at 400x520; a real Windows screenshot of the deployed r23 shows the list, the test buttons, the subscription and the start of the add form
in the 719 px window. Screenshots: artifact "Экраны VPNRouter r23".

Not run: the screenshot gate, the live-update gate, hover with a real cursor; Android connected/error ring colours with a real tunnel and the Pixel (Android 16).
