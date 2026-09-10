# Repository cleanup and README maintenance

## Why

Owner requested closing PRs and removing unnecessary branches while retaining useful work, and updating both READMEs with repository-readme and anti-slop. Micro-Spec approved explicitly on 2026-09-10.

## What and contract

Base: accepted main `065a2083545c85b788b302d8057be73b292f8d93`. Inventory open PRs #232, #233, #235, #237, #240, #253, #254 and local/remote branches. Classify dependencies, duplicate changes, CI and merge readiness. Present the concrete merge/closure list to owner before executing it. Delete only approved branches with evidence that work remains reachable or has been accepted elsewhere; record exact tips first. Preserve main, gh-pages, unique work, user files and historical plans. Update README.md and README.ru.md from actual main behavior rather than pending PR claims.

## How

Read-only inventory first; verify PR ancestry and accepted merge records, not branch age or titles. Review overlapping security PRs and dependent documentation PRs separately from large product changes. Maintain exact-SHA evidence. Implement narrow bilingual README corrections and prose cleanup without new product features. No blind conflict resolution, infrastructure changes, tags or releases. Owner subsequently authorized continued useful integration of #233/#235/#237/#240 with reviewed conflict resolution and green CI; substantial behavior changes require a separate decision.

## Risk and rollback

Primary risk is deleting unmerged work or silently accepting stale PRs. Retain ambiguous branches, compare immutable tips immediately before each approved deletion and preserve recovery records. Revert task-owned documentation commits if needed; no force pushes or broad cleanup. Unrelated `.dsh/performance-autoresearch/` and `vpn_issue_report.md` remain untouched.

## Checks and six gates

1. Build: N/A for documentation/inventory-only work; product merges require their own actual CI evidence.
2. Tests: prior README local link/anchor and diff whitespace checks passed; prior cleanup head `487c4637` CI green. Later cleanup documentation still requires its own diff checks and exact-head CI after any push; no builds in this reconciliation.
3. Documentation: bilingual README corrections independently source-reviewed PASS; inventory/outcome reconciled with accepted #232/#254 and closed #253. Bilingual desktop HTTP(S) URL note and candidate/decision ledger updated to accepted behavior.
4. Review: README source-review PASS is documentation evidence, not runtime verification. Final #232 `4b082e34` and #254 `23409668` corrective reviews PASS; unresolved PR dispositions remain outside that acceptance.
5. UI/remote: N/A; no UI implementation or runtime deployment in cleanup reconciliation.
6. Integration: approved merged-tip deletions recorded with recovery receipts; #232/#254 merged and #253 closed after preservation comparison. #235/#237/#240/#256 are now merged (exact receipts below); #233 remains under integration verification.

## Outcome

Current GitHub merge receipts rechecked on2026-09-10: #256 ae51a93d91773a48408c9fd699d8c496596bd907; #235 fa7685c53ac495f5e31fb3fec5fb3fd6cde94309; #237 673cedac4ae6ee8d5b533950c878b2e4d1a5102e; #240 f4b75f18b4ed6c2ced48e76735ff2ee8f9b58d21. Only #233 remains open. Its reviewed product c81c514e has green CI and bounded Windows/Linux/macOS receipts, but Android verification and final merge acceptance remain pending. See pr233-platform-verification-progress-2026-09-10.md. Owner ordering is finish remaining merge before broader combined-main verification. No additional branch deletions have occurred since the recorded42 remote/13 local cleanup.

IN PROGRESS. Brief commit 1bad51a9, PR #256: all four CI checks passed. Owner approved deletion of the 44 exact merged-tip candidates. Revalidation retained two registered-worktree branches; atomic exact-SHA leased deletion removed 42 remote branches, then 13 matching local copies. Fresh remote listing confirmed absence and unchanged main/retained gh-pages. Full recovery tips and receipts are in repository-cleanup-recovery-2026-09-10.md; candidate names and PR disposition in repository-cleanup-candidates-2026-09-10.md.

Both READMEs now correct absolute privacy/reproducibility claims, local crash reporting, Android permission scope, supported routing modes, Linux/macOS split kill-switch limits and bare-hash sidecar instructions. Removed static LOC/test badges; prior local links/anchors and git diff --check passed. Independent source review of the corrections PASS; prior cleanup CI on `487c4637` green. The two README ledger items are resolved for documentation/source-review scope only, without a runtime, reproducibility or deployment claim. No product code changed in cleanup documentation. The README URL note and candidate ledger now reflect accepted #254 behavior and the latest owner decisions; remaining product/documentation PR reconciliation continues separately.

PR #232 final `4b082e34` passed corrective review and CI `34487206556` (Ubuntu 3038 passed / 57 skipped; Windows 199 passed; all four checks green), then merged as `9854399d`. Standard-library query/authority parsing and generic diagnostics were restored, retaining only span scheme filtering. Runtime testing withdrew the bracket-suffix rejection overclaim because System.Uri accepts the tested suffix; other query/path/log regressions are fixed. Historical custom-parser benchmarks remain superseded, not evidence for the narrowed implementation.

PR #254 final `23409668` passed corrective review and CI `34489036398` (Ubuntu 3068 passed / 57 skipped; Windows 229 passed; all four checks green), then merged as `1d21404a`. Fresh GitHub API test comparison confirmed #253 data: rejection and blank-name HTTP(S) cases preserved; #253 closed, branch retained. The three #232 and two #254 ledger items are resolved with that exact evidence.

Cleanup remains IN PROGRESS. #235/#237/#240/#256 are now merged (exact receipts below); #233 remains under integration verification. The 42 remote and 13 local merged-branch deletions above have recovery records; registered-worktree branches and all unique/deferred work remain retained. Unrelated release/external acceptance defects remain unchecked, with no waivers. Further PR disposition or deletion requires the concrete owner decision, not automatic acceptance based on CI.
