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
3. Source proof: IN PROGRESS. Lead reopens all accepted file/line evidence.
4. Report integrity: PENDING. Check references, finding IDs, no secrets/emoji,
   Markdown whitespace and task-only diff.
5. Independent review: IN PROGRESS. Read-only reviewers; no product edits.
6. Delivery: PENDING. Report, release-gating ledger, scoped commit/PR and exact
   documentation-head checks. Build/visual/characterization reruns locally N/A
   for documentation-only delta; baseline CI is not a reproduction of findings.

## Outcome

Pending synthesis. Proposed fixes remain recommendations, not implemented changes.
