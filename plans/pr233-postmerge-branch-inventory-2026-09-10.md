# Post-merge branch cleanup inventory

Read-only snapshot while PR233 remains open; baseline origin/main f4b75f18b4ed6c2ced48e76735ff2ee8f9b58d21, task head c81c514e3cc2e103261155a1e7eb3f25e927b2c9. GitHub open PR list contains only233. No refs or worktree metadata changed by this inventory.

## Local heads with zero commits outside accepted main

Candidates only; refresh exact OIDs and existing owner deletion approval before action. Preserve main and active task branch. Worktree-bound heads excluded from ordinary deletion.

- dsh/document-gray-zones f1146be5
- dsh/fix-night-audit-gemini-2026-09-04 8de19712
- dsh/overnight-audit-2026-09-04 4694e88c
- dsh/perf-sharelink-and-query-parsing 4b082e34
- dsh/repository-cleanup-20260910 b4f3c253
- sentinel/enforce-http-https-url-validation-16166354341638659474 23409668

Zero-ahead but worktree-bound: dsh/desktop-pictogram-preview93fda2c9 and dsh/ui-pictogram-catalogfe17014d. Registrations report absent gitdir targets; do not broad-prune or remove merely because Git labels prunable. Existing approval archived only the earlier VPNCTL completion registration, not these registrations.

## Preserve until unique-work disposition

Nonzero-ahead is an ancestry fact, NOT proof useful content is absent from main: squashed/reworked patches may exist. No deletion or patch-equivalence claim.

- deferred Android f4d5265c:13 ahead
- NIGHT RED verification7f21286d:8 ahead
- PR196 clean cfedf86f:4 ahead
- CLI stop identity632f44d8:6 ahead
- FakeIP RED c36e5034:21 ahead
- old combined verification1fe721a9:16 ahead
- packaging RED0f85cbfe:5 ahead
- update-checker escaping a3dea4ae:3 ahead
- perf-autoresearch/vpnrouter-core b3985ba9:12 ahead
- pr189094b6efc:4 ahead
- pr2131da0da1d:10 ahead
- test-squash-211e28ab503:1 ahead

The seven remaining auxiliary worktree registrations (pictogram pair, NIGHT RED, deferred Android, FakeIP RED, combined, packaging RED) all report missing gitdir targets. Branches/commits preserved. Active #233 has29 commits not reachable from current main; never delete before accepted merge and fresh ancestry check. Local main43138f7c is75 commits behind accepted tracking main and has zero unique commits; do not reset user branch blindly. Unrelated untracked .dsh/performance-autoresearch/ and vpn_issue_report.md retained.

Next: finish authorized233 integration; refresh main and inventory; use narrow approved deletion with recovery OIDs, and request explicit disposition for ambiguous/unique work or remaining stale registration cleanup. Broad combined-main audit remains after merge per owner ordering.
