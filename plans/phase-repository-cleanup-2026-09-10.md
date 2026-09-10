# Repository cleanup and README maintenance

## Why

Owner requested closing PRs and removing unnecessary branches while retaining useful work, and updating both READMEs with repository-readme and anti-slop. Micro-Spec approved explicitly on 2026-09-10.

## What and contract

Base: accepted main `065a2083545c85b788b302d8057be73b292f8d93`. Inventory open PRs #232, #233, #235, #237, #240, #253, #254 and local/remote branches. Classify dependencies, duplicate changes, CI and merge readiness. Present the concrete merge/closure list to owner before executing it. Delete only approved branches with evidence that work remains reachable or has been accepted elsewhere; record exact tips first. Preserve main, gh-pages, unique work, user files and historical plans. Update README.md and README.ru.md from actual main behavior rather than pending PR claims.

## How

Read-only inventory first; verify PR ancestry and accepted merge records, not branch age or titles. Review overlapping security PRs and dependent documentation PRs separately from large product changes. Maintain exact-SHA evidence. Implement narrow bilingual README corrections and prose cleanup without new product features. No automatic conflict resolution, product migration, infrastructure change, tags or releases.

## Risk and rollback

Primary risk is deleting unmerged work or silently accepting stale PRs. Retain ambiguous branches, compare immutable tips immediately before each approved deletion and preserve recovery records. Revert task-owned documentation commits if needed; no force pushes or broad cleanup. Unrelated `.dsh/performance-autoresearch/` and `vpn_issue_report.md` remain untouched.

## Checks and six gates

1. Build: N/A for documentation/inventory-only work; product merges require their own actual CI evidence.
2. Tests: pending narrow Markdown path/anchor/command checks and diff whitespace validation; exact PR CI after pushes.
3. Documentation: pending bilingual README consistency, grounded prose, inventory and outcome.
4. Review: pending independent safety/classification and README fact checks.
5. UI/remote: N/A; no UI implementation or runtime deployment.
6. Integration: pending fresh PR/branch state checks and approved-action receipts; preserve every unique change.

## Outcome

IN PROGRESS. No PR closed, branch deleted or product PR merged by this task. Initial current-main check-runs were green. PR #240 conflicts; #237 inherits #235; #253/#254 overlap but differ in error feedback. These observations are not merge approval.
