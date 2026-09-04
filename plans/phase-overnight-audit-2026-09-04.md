# Overnight audit: scope and evidence contract

## Why

Owner-requested deep security, lifecycle, performance and UI-state audit, following
`plans/overnight-mission-gpt6-astra-2026-09-04.md`. Deliver an actionable Russian
morning report, not a linter scorecard or speculative fixes.

## What

- Read the canonical contract, OPEN-DEFECTS history and subsystem maps first.
- Investigate lifecycle/session isolation; DNS and Unix firewall invariants;
  health/probe resource bounds and truthful UI state.
- Source-verify independent reviewer findings; record survivors in OPEN-DEFECTS.
- Write the report with exact baseline line ranges, reproduction recipes,
  minimal remedies, regression-test proposals and the three morning priorities.
- No product-code edits, live VPN/UI scenarios, deployments, releases or merges.

## Baseline and branch

Audit source: `9f8c8b5a8b34f264762294f8a11842b4edab90a9`, supplied checkout
`dsh/document-gray-zones` (open PR #235). The difference from accepted
`origin/main` (`b7ce0e4f140b7ed4257673aa67a2b359c535ef7f`) is documentation only.
Preserve that explicitly referenced mission baseline; do not switch out its maps.
Task branch: `dsh/overnight-audit-2026-09-04`. This depends on PR #235; do not
attribute its inherited documentation changes to this audit.
Existing untracked `.dsh/performance-autoresearch/` and `opendesign/` are excluded.

## How

Lead owns architectural analysis and final acceptance. Two independent read-only
reviewers investigate network and background-resource/UI slices; a third
challenges the lead's lifecycle hypotheses. Ordinary DSH subagents are used;
no scripted workflow or alternative-model routing is claimed.
All confirmed claims must survive reopening the owning source and its callers.
Tests that merely pin a string are not behavioral evidence.

## Risks and rollback

False positives and exaggerated runtime claims are the primary risks. Explicitly
separate source proof, proposed reproduction, observed CI and unexecuted tests.
Never include live configuration, credentials or subscription material.
Rollback: revert only task documentation commits; no runtime state is modified.

## Tests and six gates

1. Baseline identity/CI: PASS. Exact source SHA has successful test,
   characterization-windows, grep and go-test-windows checks.
2. New behavioral reproduction: BLOCKED. `harness-test` has no dotnet and cannot
   build/run VPNRouter. Read-only Linux-worker preflight confirmed `debian-xfce`,
   tester, sufficient resources, no listed compiler jobs, but no dotnet SDK.
   No SDK installation or worker mutation authorized/performed.
3. Source proof: PASS. Lead reopened accepted file/line evidence. Twelve product
   findings: eight P1, four P2; separate P3 map drift and a measurement candidate.
4. Report integrity: PASS. git diff --check; 12 unique product IDs, 8 P1/4 P2,
   ledger parity, qualified referenced paths and no-emoji output checks passed.
   Only the report, this brief and OPEN-DEFECTS belong to the audit delta.
5. Independent review: PASS. Network/background/lifecycle source reviews and
   final artifact review found no blocking factual defects. Supplemental
   ConnStats/authenticated-observer findings independently source-checked.
6. Delivery: Brief e5291d09 pushed; all four exact-head checks PASS.
   PR #237 open with PR #235 dependency disclosed. Report/ledger prepared for
   covering commit. Its exact-head CI receipt will be recorded in PR #237 and
   the final response after push, not pre-claimed inside its own commit.
   PowerShell is unavailable here; exact-SHA GitHub check-runs were read via gh
   without waivers. Local build/visual reruns N/A for documentation delta;
   existing CI does not reproduce new findings. No new C# tests were executed.

## Outcome

Russian report: `plans/overnight-audit-morning-report-2026-09-04.md`.
All confirmed defects and the explicitly labelled research follow-up are in
`plans/OPEN-DEFECTS.md`. Source proof includes call chains, failure prerequisites,
negative controls and proposed deterministic tests. No P0 was established.
Three priorities: destructive/DNS boundaries; confirmed Unix cleanup plus current
endpoint snapshot; lifecycle intent/readiness/commit semantics. Product code and
live systems remain unchanged; fixes remain recommendations, not implementation.
No release, waiver, tag, deployment or merge performed.
