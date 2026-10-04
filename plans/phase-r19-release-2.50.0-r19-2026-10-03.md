# R-19: the visible redesign, candidate v2.50.0-r19

## Why

The owner (2026-10-02) saw no clear improvement in r17/r18 except tab icons, called the Simple-mode home screen the ugliest, most asymmetric and most illogical screen, and asked for a real
redesign. Since the r18 tag, main carries D-15a (brand area and main tab strip, #476), D-15b (Servers and Subscribe as designed lists, #477), D-15c (one look for inputs, small motion, #479),
D-16 (the home screen: status emblem, one dominant Connect button, connection card, #478), and the probe fix for dark-theme renders (#475). The owner's standing mandate covers the release.
This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r19` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate (18 assets,
   integrity, strict CI), `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`: the cold cycles drive the new home screen through UI Automation and find the button by the names Connect / Подключить and
   Disconnect / Отключить, so this is the live check of the redesigned Connect button. Then a fast-click run on the deployed build.

## Not covered

Stable cut, the previous-stable to candidate live-update gate; hover states with a real cursor; the Android app (own earlier design); the Windows service / GUI ownership problem (ledger);
the real update that confirms the driver-lock fix.

## Verification

Exact-head CI on this PR; a full audit (`tools/ui-mcp/audit.py`) on the MCP binary built from the merged main must report zero warnings before the tag; the steps above are recorded in the Outcome.

## Outcome

Published 2026-10-03 as prerelease `v2.50.0-r19` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `5e8946bc`, 18 assets. The six Windows files (unsigned: no SignPath secret exists)
were built on `windows-worker` and uploaded only after size, SHA-256 and sidecar matched the worker's copies; macOS, Linux and Android came from the tag workflows. Strict CI gate and the
waiver gate as in r18.

Post-ship on WINBRAT with `tools/post-ship-local.ps1`: `POSTSHIP-LOCAL: PASS` (version 2.50.0-r19, commit `5e8946bc`, 2 cold cycles). A real Windows screenshot of the deployed build then
showed the default window (640 px) too low for the redesigned home screen; fixed by D-17 (#481) and shipped in r20.
