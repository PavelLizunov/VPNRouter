# R-17: design and crash-safety candidate v2.50.0-r17

## Why

The owner tested r16 on 2026-10-02: install worked on Windows and Android, the Applications page was still the legacy one, and the desktop app ended abruptly
during fast tab switching (no crash information reached the bundle). Since the r16 tag, main carries D-6 (inner tab strips, #457), D-7 (Applications page,
#461), D-8 (audit of every page and window, #462), D-9 (web installer no longer wipes the installation before the new payload is checked, #463) and D-10
(UI exception guard, crash reports and crash index in the diagnostics bundle, installer launches the app as the elevated user, localized True Split text,
#464), plus the UI exploration MCP (#460, tooling only). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`; the owner's standing mandate
covers the release.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r17` (this PR).
2. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag
   workflows, prepublication gate (18 assets, integrity, strict CI), `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
3. Post-ship on WINBRAT with `tools/post-ship-local.ps1`.

## Not covered

Stable cut, the previous-stable to candidate live-update gate; the cause of the owner's abrupt exit is unproven (the brief
`plans/phase-d10-r16-owner-logs-2026-10-02.md` lists what is needed); the Windows service / GUI multi-owner behavior (ledger WIN-SERVICE-GUI-MULTI-OWNER);
hover states with a real cursor; Android on a Pixel with Android 16.

## Verification

Exact-head CI on this PR; the steps above are recorded in the Outcome as they complete.

## Outcome

Published 2026-10-02 as prerelease `v2.50.0-r17` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `78596140`, 18 assets. The six Windows files (unsigned: no SignPath secret exists) were built on `windows-worker` and uploaded only after size, SHA-256 and sidecar matched the worker's copies; macOS, Linux and Android came from the tag workflows. Before publication: tag-bound `test`, `test-update`, three platform builds and the dispatched integrity run green, strict CI gate `OK` (8 green, 0 red), `check-open-p0.ps1` with the recorded waiver (same three owner-gated P1 lines).

Post-ship on WINBRAT with `tools/post-ship-local.ps1`: `POSTSHIP-LOCAL: PASS` on the first attempt (version 2.50.0-r17, commit `78596140`, 2 cold cycles). A further 600 fast UI Automation selects (about 8 per second, all tabs, sub-tabs and the Simple/Advanced toggle) on the deployed r17 left the app alive and wrote no crash report.
