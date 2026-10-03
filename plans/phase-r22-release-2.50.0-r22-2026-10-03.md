# R-22: tester interface fixes and the Android home ring, candidate v2.50.0-r22

## Why

The interface half of the tester's report on r20 (items 1, 4, 5, 6, 7, 8 of the session artifact "Замечания тестера r20"): the narrow window and the missing window floor (F-7), the "Change" form that pushed the emblem down (F-6), the hidden setup wizard (F-8), and the
Android home that did not change (A-8, with the owner's request that the circle can be tapped). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r22` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, a real screenshot of the new home and of the smallest window, and the Android APK checked on the emulator.

## Not covered

Android connected/error ring colours with a real tunnel and the Pixel (Android 16); showing the setup check automatically at the first run; stable cut, live-update gate, hover with a real cursor.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Pending.
