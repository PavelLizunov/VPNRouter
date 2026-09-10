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

IN PROGRESS. Brief commit 1bad51a9, PR #256: all four CI checks passed. Owner approved deletion of the 44 exact merged-tip candidates. Revalidation retained two registered-worktree branches; atomic exact-SHA leased deletion removed 42 remote branches, then 13 matching local copies. Fresh remote listing confirmed absence and unchanged main/retained gh-pages. Full recovery tips and receipts are in repository-cleanup-recovery-2026-09-10.md; candidate names and PR disposition in repository-cleanup-candidates-2026-09-10.md.

Both READMEs now correct absolute privacy/reproducibility claims, local crash reporting, Android permission scope, supported routing modes, Linux/macOS split kill-switch limits and bare-hash sidecar instructions. Removed static LOC/test badges; local links/anchors and git diff --check passed. Independent source review confirmed defects; follow-up review pending for last corrections. No product code changed. Existing stale feature/architecture counts and other README sections still need bounded review.

No open PR closed or merged. #233/#235/#237/#240 conflict with main; #232/#253/#254 are behind, with green checks on their own heads only. All unique/deferred branches, user files and worktree registrations retained. Further PR disposition requires the agreed concrete owner decision, not automatic acceptance based on old CI.
