# Planning instructions

Follow [the project contract](../docs/agent-contract.md) and root [AGENTS.md](../AGENTS.md).
Keep task briefs, outcomes and the defect ledger under `plans/`. Durable
references and runbooks belong in `docs/`.

## Task records

- Use `phase-<slug>-YYYY-MM-DD.md` for a task brief; update the same record through delivery.
- Record intent, scope, invariants, verification, unknowns and observed outcomes.
- Release-note drafts remain gitignored.
- A dated plan or receipt is evidence for its named snapshot, not a current
  contract, open-defect status or release approval.

## History

Historical plans, reports, evidence and designs were moved out of the tree on
2026-09-28 (H-1). They remain in Git: read one with
`git show 6491be4c:plans/<file>` or list them with
`git ls-tree -r --name-only 6491be4c plans/`. A code comment or ledger entry that
cites a missing `plans/` file refers to that snapshot. Do not restore history
files into the tree as current guidance.

## References

- [OPEN-DEFECTS.md](OPEN-DEFECTS.md): release-gating ledger; record verified findings and their disposition.
- [../docs/execution-methodology.md](../docs/execution-methodology.md): task lifecycle and verification.
- [../docs/interaction-contracts/README.md](../docs/interaction-contracts/README.md): Free Configs and Applications interaction contracts.
- [../docs/release-strategy.md](../docs/release-strategy.md): rolling-candidate policy.
- [../docs/cut-stable-checklist.md](../docs/cut-stable-checklist.md): fixed-WINBRAT live-update gate.
- [../docs/code-signing-signpath-runbook.md](../docs/code-signing-signpath-runbook.md): signing runbook; recheck prerequisites.

## Boundaries

Use matching project skills and the global Harness delegation policy. Do not
hardcode model routes, worker credentials or volatile host addresses in plans.
Worker roles and preflight belong to [docs/test-workers.md](../docs/test-workers.md).
