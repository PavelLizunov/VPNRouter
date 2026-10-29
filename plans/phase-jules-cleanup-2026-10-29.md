# Phase - Jules findings and duplicate PR cleanup

- Owner: DSH lead; direct owner instruction: fix the confirmed findings and clean up the Jules PR backlog.
- Branch: dsh/jules-findings-cleanup-20261029
- Base: f6b39dbfc4d4de6e064b470037ea6b5a312c5a4c
- Risk: MEDIUM; secret redaction and platform process arguments need regression coverage.
- Blast radius: diagnostic text masking, affected folder/helper launchers, their tests and defect records. No VPN routing, capability, updater policy, release or UI layout change.
- Rollback: revert the scoped implementation commit; closed PRs retain their branches/history and canonical replacement links.

## Why

Repeated Jules/Sentinel runs created 51 open PRs, 41 about the same still-open LOG-REDACT-QUOTED-SPACE defect. Closing PRs alone would leave the defect and recurring source of noise intact. Preserve useful findings without importing unrelated changes or exaggerated security claims.

## What

- Fix quoted-secret masking in DiagnosticsRedactor, preserving key names, quote style, non-secret fields and existing unquoted/authorization behavior. Handle escaped delimiters/backslashes, opposite quotes, commas, empty and unterminated values, and multiple fields on one line.
- Verify the separate header-protection-key coverage gap from #631 and add explicit aliases only if confirmed; do not import its unrelated capability bypass or Sweeper changes.
- Verify Linux OpenLogs (#546) and reuse the existing FileManagerHelper selection if appropriate. Triage hosts helper and SelfRepair argument changes against actual generated-path callers; reject unsupported command-injection claims and broad path denylists.
- Add regression tests through production helpers and real diagnostic/crash-report consumers where safe, using only synthetic secrets and isolated temporary directories.
- Publish a verified replacement PR, then close Jules duplicates/superseded or non-actionable proposals with explicit reasons. Leave Dependabot, other project PRs and historical merges intact. No remote branch deletion.
- Pausing recurring Jules tasks is separate: no Jules/GitHub browser profile is currently approved, and the repository installation endpoint requires an unavailable app JWT. Do not claim scheduler shutdown based on PR closure.

## How

1. Save/commit this brief and publish its PR; inspect exact-head CI before implementation.
2. Source-verify residual findings and record new confirmed root causes in OPEN-DEFECTS before fixes. Existing quoted-space ledger entry is authoritative, not a new defect.
3. Implement the smallest coherent fixes and production-path regressions; obtain independent correctness, test and security review.
4. Use an exact-SHA preflighted worker or GitHub CI. Run red/green regression, focused checks, Release solution build and the full applicable suite. Preserve unrelated failure evidence rather than weakening assertions.
5. Update Outcome and PR disposition evidence, push checkpoint, wait for exact-head green checks, then close the replaced backlog. Merge/release remain outside this task unless explicitly authorized.

## Verification

1. Build: PENDING - Release solution build on a preflighted worker; no local SDK provisioning.
2. Tests: PENDING - regression red/green, focused redaction/launcher tests and full applicable suite. Record CI exclusions separately.
3. Docs: PENDING - defect ledger and final Outcome/dispositions.
4. Review: PENDING - independent correctness/test/security lenses; coordinator verifies findings.
5. UI: N/A - no layout, bindings or shipped binary change; launch selection checked without starting a desktop application.
6. Characterization/integration: PENDING - public surface unchanged; consumer-level redaction and launch argument tests.

## Outcome

- Status: IN PROGRESS.
- Original dirty checkout preserved; separate worktree from current main.
- Prior review confirmed the quoted-space defect; source blob unchanged since the 2026-10-25 native-regex triage. No full local .NET execution claimed.
- Worker request vpnrouter-jules-cleanup-20261029 is queued for Linux101 behind the current owner's Windows lease. GitHub CI remains available independently.
- No PR or Jules schedule has been changed yet.
