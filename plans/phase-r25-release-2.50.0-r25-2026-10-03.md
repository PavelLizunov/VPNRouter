# R-25: a natural Home button and a home screen that fits its window, candidate v2.50.0-r25

## Why

The owner's feedback on r24: a button's bottom is cut off on part of the screenshots (the last home row did not fit the 760 px window) and the outlined "Simple" pill is unnatural, has no icon, stands out. D-21 (#507) fixes both. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r25` (this PR).
2. Contents since r24: D-21 (house icon plus "Home" on a quiet tint on desktop and Android, tighter home screen, default window 820 px).
3. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, real screenshots, the Android APK on the emulator.

## Not covered

Android Zapret/Telegram screens and the Android cards below the ring; the Pixel; stable cut, live-update gate.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Pending.
