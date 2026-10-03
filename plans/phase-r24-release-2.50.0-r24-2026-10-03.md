# R-24: Zapret and Telegram pages redone, visible way back, one segmented look, candidate v2.50.0-r24

## Why

The owner tested r23 ("on Android it got much better") and asked for the next round in one cycle: the way back to Simple mode is hardly visible (Android Advanced header, desktop pill), and the Zapret and Telegram proxy pages must be as beautiful as the home screen. This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r24` (this PR).
2. Contents since r23: D-19 (#503: HomeKit styles, StatusEmblem, Zapret/Telegram/Tools pages, outlined "Simple" pill on desktop and Android), D-20 (segmented strips on Servers, Public, Applications), M-1c hover/press probe steps (#502).
3. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, real screenshots of the Zapret and Telegram pages, the Android APK from the tag on the emulator.

## Not covered

Android Zapret/Telegram bodies, the Android cards below the ring, the Pixel; stable cut, live-update gate.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome.

## Outcome

Pending.
