# Desktop pictogram integration and WINBRAT preview

## Why / approval
Owner accepted catalog PR #246 and approved the Windows-preview Micro-Spec on 2026-09-07. Goal is visual inspection before any prerelease. Base fe17014d includes the accepted catalog. No merge/release authorized.

## What / invariants
Integrate accepted Desktop UI pictograms including Telegram and DPI/Zapret hero icons. Preserve commands, bindings, visibility, labels, accessibility and layout intent. Brand/logo/mascot/app/tray/platform assets remain byte-identical. Android integration and tester-report network fixes are excluded.

Build exact committed Windows source and deploy to fixed windows-worker/WINBRAT in an isolated test directory and interactive tester session. Do not launch VPN/zapret automatically, import tester secrets, modify the developer installation or pretend executable-directory isolation guarantees configuration isolation. Resolve existing config isolation before deployment; fail closed if unavailable without broader changes.

## How
1. Brief-first commit/PR/CI; inspect resources and deployment isolation read-only.
2. Reuse Avalonia geometry/control facilities and accepted paths, no new dependency. Integrate only Desktop consumers and add focused contract/headless checks.
3. Commit exact reviewed snapshot, Windows Release build/tests, independent review, brand diff verification.
4. Deploy checksummed output using supported fixed-WINBRAT path; verify process identity, version and rendered disconnected UI; leave it available for owner review with explicit cleanup follow-up.

## Risk / rollback
UI binding/layout regression and accidental shared configuration/autostart. Verify isolated configuration and no conflicting engine before launch; never use tester diagnostic config. Rollback task branch changes; remove exact task-owned preview files after owner finishes. Do not delete unrelated files or kill unrelated processes.

## Six gates
- Build: Windows Release build exact SHA, no errors; verify SDK compatibility (worker has 10.0.302 and 10.0.400, source global.json examined before use).
- Tests: focused geometry/consumer/headless tests, existing CI, light/dark and narrow layout checks.
- Docs: outcome and deployment provenance/checksum/path/session, cleanup instructions.
- Review: source-verified independent correctness/coverage review; resolve findings.
- UI: fixed WINBRAT interactive disconnected launch only, isolated non-secret state; no live VPN test or release claims.
- Characterization: preserve existing member contracts and brand/platform asset hashes; no public behavior change.

## Outcome
IN PROGRESS. No integration/build/deployment completion claimed.
