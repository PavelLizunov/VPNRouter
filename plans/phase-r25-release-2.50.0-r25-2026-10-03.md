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

Published 2026-10-03 as prerelease `v2.50.0-r25` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `4ea2d0e3`, 18 assets, same gates as r21-r24 (strict CI gate `OK`, 8 green, waiver gate, integrity run green before and after
publication, `POSTSHIP-LOCAL: PASS` on the first attempt, 2 cold cycles). Contents: D-21 #507. The terminal of the session was closed during the Windows build; the build kept running on the worker (it was started through WMI), the lock was released and
the artifacts were verified and uploaded afterwards (size, SHA-256 and sidecar matched for all three files).

The UI MCP cut-off lint is clean on the home screen at 520x820 in English and Russian (at 760 px it reported the last row cut by 24 px, the owner's "bottom of the button is cut"). A real Windows screenshot of the deployed r25 shows the "Home" button with the house icon in the
Advanced header, and the Android emulator shows the same button in the Advanced header (test-hook APK of the D-21 commit). Screenshots: artifact "Экраны VPNRouter r25".

Not run: the screenshot gate, the live-update gate; the test machine screen is 1024x768, so on it the window is still shortened and the lowest home rows scroll; Android Zapret/Telegram screens and the cards below the ring were not redone; the Pixel and a real tunnel on Android were not checked.
