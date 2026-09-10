# Combined main regression verification

Status: proposed, execution deferred until integration completes. Owner clarified on 2026-09-10: finish merging first, then proceed to broader verification. Resume #233 under existing bounded review/green-CI acceptance; no waiver of its ordinary gates. Refresh candidate to final accepted main (including #233 if accepted) and review the concrete verification scope before starting. Branch deletion still requires preservation checks and applicable owner approval. This is verification, not an autonomous optimization campaign or release authorization.

## Snapshots

Candidate: accepted main f4b75f18b4ed6c2ced48e76735ff2ee8f9b58d21. Fetch and verify before execution; record any later main movement separately, never silently move target. Resolve the pre-integration baseline from first-parent history before #232/#254/#256/#235/#237/#240; record full SHA and actual tree differences. Use identical external fixtures for both snapshots. Intentional behavior changes need explicit expected-output differences, not automatic parity assertions.

## Phase 1 — impact and functional contracts

Build a map of overlapping changes and owner-approved behavior. Prioritize parser -> subscription import -> persistence/UI, strict/custom DNS -> routing config, start -> typed readiness -> failover -> stop/restart/apply, cancellation -> statistics/telemetry and owned process/firewall cleanup. Include release tooling/package regression contracts. Keep existing P1 follow-ups visible and distinguish pre-existing from introduced defects.

On exact-SHA, preflighted workers run focused cross-PR scenarios and full supported test gates. Add deterministic negative controls for stale session, malformed input, canceled operation, failed stop and foreign listener. Test outputs must show selected/executed/skipped counts. For a confirmed defect reproduce red in isolated baseline/candidate worktrees without reverting the user's checkout. No live VPN/firewall/TUN/deployment in this phase. Headless synthetic UI only; native unsupported scenarios marked unverified.

## Phase 2 — anti-slop audit, no automatic edits

Use anti-slop AFTER mode and repository-readme. Audit README.md/README.ru.md, current subsystem maps and changed user-facing text against actual source and evidence: unsupported privacy/security/performance claims, stale examples, links/anchors, EN/RU differences and misleading statuses. Inspect changed UI empty/loading/error and disabled states with synthetic fixtures. Report numbered HIGH/MEDIUM/LOW findings in plans/anti-slop-audit-2026-09-10.md. No whole-UI accessibility or click-through PASS without actual checks; absent checks are explicitly unverified. Specific findings require approval before edits.

## Phase 3 — performance regression characterization

Use performance-autoresearch measurement discipline, not its optimization loop. No candidate optimization edits, no reuse/mutation of unrelated .dsh/performance-autoresearch state. Read skill README before creating evaluator receipts. Freeze workload generators, correctness oracles, negative controls, toolchain and measurement policy before measurement.

Compare baseline vs candidate on same preflighted worker with warmups and balanced AB/BA paired repeats. Initial budget: three workloads, six pairs each, at most one extension to twelve pairs for inconclusive noise. Workloads: mixed valid/invalid share-link import (small/large batch), representative generated/custom config processing, synthetic lifecycle/probe concurrency with deterministic fake transports. Measure wall time, managed allocation where available, peak RSS and relevant concurrency counts. UI startup/idle measurements only if representative harness exists; otherwise propose separately, do not substitute mocked time for real UI latency.

Retain all raw samples and median paired deltas/MAD. Establish practical regression thresholds before measurement based on metric resolution and user-visible budgets; do not pick thresholds after seeing results. Report regression/no detected regression/inconclusive per workload, never equate noise with parity. Native network throughput is outside this non-VPN plan. If profiling explains a confirmed regression, propose one bounded fix separately; no opportunistic optimization.

## Evidence and acceptance

Each result records exact SHAs, worker identity/toolchain, command, exit status, fixture hash and artifact path; redact secrets. Independent final review checks cross-PR coverage, contradictory claims, skipped gates and evidence binding. Deliver a single table: scenario, expected behavior, baseline/candidate result, evidence, limitation. Stop verification acceptance on confirmed newly introduced important defects; preserve evidence and seek corrective scope. Existing defects are neither erased nor waived.

Pass means the enumerated checks and thresholds passed on the tested target, not absence of all regressions. No merge, release, infrastructure change, policy grant, SDK installation or broad cleanup follows automatically. Owner receives findings and makes next integration decision.
