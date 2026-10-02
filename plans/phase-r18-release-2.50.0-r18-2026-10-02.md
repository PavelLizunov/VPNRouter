# R-18: design candidate v2.50.0-r18

## Why

The owner asked (2026-10-02) to check every page of the app in every size for fit, breakage, symmetry and design errors. Since the r17 tag, main carries D-11 (contrast, #466),
the audit tooling (#468), D-12 (token text boxes, compact main tabs, servers link row, update/rules texts, #469), D-13 (the fixed width rule for the compact tabs, #470), D-14
(dark success button text, success text contrast, stacked rule combos, #472) and the evidence notes of the owner's crashes folder (#471). The owner's standing mandate covers the release.
This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r18` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate
   (18 assets, integrity, strict CI), `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, plus a fast-click run on the deployed build.

## Not covered

Stable cut, the previous-stable to candidate live-update gate; Servers and Subscribe redesign; real Windows font rendering (the audit probe uses Linux fonts); only the first level of
inner tabs was audited; the service/GUI ownership problem (ledger WIN-SERVICE-GUI-MULTI-OWNER); Android on a Pixel with Android 16; the real update that confirms the driver-lock fix.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-02 as prerelease `v2.50.0-r18` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `be9f3797`, 18 assets. The six Windows files (unsigned: no SignPath
secret exists) were built on `windows-worker` and uploaded only after size, SHA-256 and sidecar matched the worker's copies; macOS, Linux and Android came from the tag workflows.
Before publication: tag-bound `test`, `test-update`, three platform builds and the dispatched integrity run green, strict CI gate `OK` (8 green, 0 red), `check-open-p0.ps1` with the
recorded waiver (same three owner-gated P1 lines).

Post-ship on WINBRAT with `tools/post-ship-local.ps1`: `POSTSHIP-LOCAL: PASS` on the first attempt (version 2.50.0-r18, commit `be9f3797`, 2 cold cycles). A further 600 fast UI Automation
selects (about 8 per second, all tabs, sub-tabs and the Simple/Advanced toggle) on the deployed r18 left the app alive and wrote no crash report.

The control audit that preceded the release (`tools/ui-mcp/audit.py --tabs 1 --widths 360,520,1000`, 336 cells) had no cut-off, overlap or low-contrast finding left except the three fixed in D-14.
