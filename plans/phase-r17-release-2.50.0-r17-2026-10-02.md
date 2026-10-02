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

Pending.
