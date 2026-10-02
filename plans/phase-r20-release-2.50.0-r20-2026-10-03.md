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

Published 2026-10-03 as prerelease `v2.50.0-r20` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `c01c8b43`, 18 assets. The six Windows files (unsigned: no SignPath secret
exists) were built on `windows-worker` and uploaded only after size, SHA-256 and sidecar matched the worker's copies; macOS, Linux and Android came from the tag workflows.
Before publication: tag-bound `dotnet test`, `test-update`, the platform builds and the dispatched integrity run green, strict CI gate `OK` (8 green, 0 red), `check-open-p0.ps1` with the
recorded waiver (same three owner-gated P1 lines).

The first r20 bump PR (#482) failed CI: the home screen redesign made the view model constructor reach a static initializer that decoded the mascot through the Avalonia asset loader; in a plain
test with no Avalonia platform it threw and the failed type initializer then broke every later use of `MainWindowViewModel` in the process (`TypeInitializationException`, order dependent, so the r19
run had passed). D-18 (#484) loads the mascot on first use and treats a failure as "no image"; `MainWindowViewModelLogoTests` covers it. #482 was then updated and merged.

Post-ship on WINBRAT with `tools/post-ship-local.ps1`: `POSTSHIP-LOCAL: PASS` on the first attempt (version 2.50.0-r20, commit `c01c8b43`, 2 cold cycles). A further 600 fast UI Automation
selects (all tabs, sub-tabs and the Simple/Advanced toggle, no sleep) left the app alive, 0 errors, no crash event. Real Windows screenshots of the deployed build show the whole emblem,
Connect button and connection card; the test machine screen is only 1024x768, so the window is shortened to 719 px there and "Advanced settings" sits below the fold with a scroll bar.

Not run: the screenshot gate, the live-update gate, hover with a real cursor.
