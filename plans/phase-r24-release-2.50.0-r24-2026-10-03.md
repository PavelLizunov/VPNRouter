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

Published 2026-10-03 as prerelease `v2.50.0-r24` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `23679561`, 18 assets, same gates as r21-r23 (strict CI gate `OK`, 8 green, waiver gate, integrity run green,
`POSTSHIP-LOCAL: PASS` on the first attempt, 2 cold cycles). Contents: D-19 #503 (HomeKit styles, StatusEmblem, Zapret/Telegram/Tools pages, outlined "Simple" pill on desktop and Android; Windows baselines for page-dpi-bypass/tools/telegram re-rendered on the worker),
D-20 #504 (segmented strips on Servers, Public, Applications), M-1c #502 (hover/press probe steps). The audit of the redesigned surfaces (dpi, telegram, tools, simple; 96 cells) had 0 warnings; the earlier full audit of r23 had 2340 cells with 0 broken.

Real Windows screenshots of the deployed r24 (WINBRAT, 536x719 window) show the Tools tab with the segmented Zapret / Telegram proxy switcher, the emblem hero, the pill button and the strategy card, and the outlined "Simple" pill. Android: the outlined "< Simple" pill in the Advanced
header was checked on the emulator with a test-hook APK of the D-19 commit (not with the release APK). Screenshots: artifact "Экраны VPNRouter r24".

Not run: the screenshot gate, the live-update gate; Android Zapret/Telegram screens and the Android cards below the ring were not redone; the Pixel (Android 16) and a real tunnel on Android were not checked.
