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

Pending.
