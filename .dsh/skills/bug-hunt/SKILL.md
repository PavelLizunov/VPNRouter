---
name: bug-hunt
description: Adversarial multi-agent review of a diff or subsystem. Verify findings against current source and record them in plans/OPEN-DEFECTS.md.
whenToUse: Before cutting stable, after a non-trivial feature/refactor, or when explicitly asked for a bug-hunt or adversarial review. Not for <=5-line hotfixes without contract or behavior drift.
---

# Adversarial bug-hunt

Read `docs/agent-contract.md`, affected zone instructions and the existing defect
ledger. Review permission alone does not authorize implementation or release.

## Inputs and routing

- Record the agreed scope, exact base/head or working-tree snapshot, task brief
  and verification limits. Include staged, unstaged and task-owned untracked files.
- Use the reviewer brief in `docs/REVIEW_AGENT_PROMPT.md`; provide the complete
  scoped diff and relevant source. Split large inputs rather than truncating them.
- Follow global Harness delegation policy. Verify the actual callable schema and
  authorized model route; do not assume generic subagent inheritance is safe.
- Use workflow only for explicitly requested workflow or large multi-agent
  orchestration when current permissions allow it. Workers do not delegate.
- If independent review is unavailable, report the limitation and request a
  decision when that review is required. Do not relabel self-review as independent.

## Procedure

1. Assign distinct correctness, concurrency/lifetime, security/fail-closed and
   test-coverage questions. Use 3-5 reviewers for a feature; larger audits need
   an explicit scope and budget. Keep reviewers read-only.
2. The coordinator reopens every cited source, traces callers and guards, and
   checks counterevidence. Classify candidates as confirmed, hypothesis,
   duplicate or rejected; model agreement is not reproduction.
3. For confirmed findings, record severity separately from evidence:
   - P0: a broken or leaky build can reach users.
   - P1: a real, bounded defect.
   - P2: hygiene or code cleanup.
4. Record verified findings in `plans/OPEN-DEFECTS.md` before implementation or
   deferral, with evidence, status and a task/PR reference. Update existing entries
   for the same root cause instead of filing duplicates.
5. Fix blockers only within authorized implementation scope. A review-only task
   returns findings; ask before expanding scope. Unresolved P0/P1 findings remain
   subject to `tools/check-open-p0.ps1` and the stable release contract.

## Output

For each finding include severity, title, problem, path:line, causal evidence,
proposed fix, cost and risk. Retain reviewer provenance when merging duplicates.
Report snapshot, checked scope, executed checks, untested limits and disposition.
Do not claim a clean review when essential source or independent evidence is absent.
